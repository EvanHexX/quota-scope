using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuotaScope.Providers;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace QuotaScope.WinUI.Windows;

// WinUI port of app/UsagePopupForm.cs: borderless always-on-top-capable popup
// rendering provider sections as bars or bento cards. Content is built in code
// and the window is sized from measured content, so layout can never clip.
internal sealed class UsagePopupWindow
{
    private const double BarsWidth = 408;
    private const double BentoWidth = 452;
    private const double BentoSingleCardWidth = 240;
    private const double BentoCardHeight = 230;
    private static readonly FontFamily UiFont = new("Pretendard, Pretendard Variable, Segoe UI Variable, Segoe UI");

    private readonly Window _window = new();
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly IntPtr _hwnd;
    private readonly AppSettings _settings;
    private readonly Border _rootBorder;
    private readonly Viewbox _scaleHost;
    private readonly Grid _headerGrid;
    private readonly TextBlock _titleText;
    private readonly Button _pinButton;
    private readonly FontIcon _pinIcon;
    private readonly Border _headerDivider;
    private readonly StackPanel _sectionsPanel;
    private IReadOnlyList<ProviderUsage> _usages = Array.Empty<ProviderUsage>();
    private string? _appliedBackdropTheme;
    private bool _menuOpen;

    public bool Visible { get; private set; }

    public event Action? SettingsChanged;

    public UsagePopupWindow(AppSettings settings, Func<MenuFlyout> menuFactory)
    {
        _settings = settings;

        _titleText = new TextBlock
        {
            Text = "Usage",
            FontFamily = UiFont,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pinIcon = new FontIcon { Glyph = "", FontSize = 14 };
        _pinButton = new Button
        {
            Content = _pinIcon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent)
        };
        _pinButton.Click += (_, _) => TogglePin();

        _headerGrid = new Grid { Height = 40, Background = new SolidColorBrush(Colors.Transparent) };
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_titleText, 0);
        Grid.SetColumn(_pinButton, 1);
        _headerGrid.Children.Add(_titleText);
        _headerGrid.Children.Add(_pinButton);
        // Header doubles as the drag handle: manual pointer-capture drag gives
        // smooth continuous movement (the WM_NCLBUTTONDOWN trick stutters under
        // WinUI's input stack).
        _headerGrid.PointerPressed += OnHeaderPointerPressed;
        _headerGrid.PointerMoved += OnHeaderPointerMoved;
        _headerGrid.PointerReleased += OnHeaderPointerReleased;
        _headerGrid.PointerCaptureLost += (_, _) => _dragging = false;

        _headerDivider = new Border { Height = 1, Margin = new Thickness(2, 6, 2, 10) };

        _sectionsPanel = new StackPanel();

        var layoutRoot = new StackPanel();
        layoutRoot.Children.Add(_headerGrid);
        layoutRoot.Children.Add(_headerDivider);
        layoutRoot.Children.Add(_sectionsPanel);

        _rootBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 14, 20, 18),
            Child = layoutRoot,
            RequestedTheme = ElementTheme.Dark
        };
        _rootBorder.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && !_settings.IsPinned)
            {
                e.Handled = true;
                Hide();
            }
        };

        SetMenu(menuFactory());

        // Viewbox handles the UI-scale option: the root lays out at its base
        // size and is stretched to the (scaled) window, so content always
        // fills the window exactly — no white edges, no clipping.
        _scaleHost = new Viewbox { Stretch = Stretch.Fill, Child = _rootBorder };
        _window.Content = _scaleHost;
        _window.Title = "Usage";
        _hwnd = WindowNative.GetWindowHandle(_window);
        _appWindow = _window.AppWindow;
        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = _settings.IsPinned;
        _appWindow.SetPresenter(_presenter);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.Closing += (_, e) =>
        {
            // Alt+F4 etc. hides instead of destroying the long-lived window.
            e.Cancel = true;
            Hide();
        };
        ApplyWindowChrome();

        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated
                && Visible && !_settings.IsPinned && !_menuOpen)
            {
                Hide();
            }
        };

        Rebuild();
    }

    public void SetUsage(IReadOnlyList<ProviderUsage> usages)
    {
        _usages = usages;
        if (Visible)
        {
            Rebuild();
            PositionAndSize();
        }
    }

    public void ApplySettings()
    {
        _presenter.IsAlwaysOnTop = _settings.IsPinned;
        Rebuild();
        if (Visible)
        {
            PositionAndSize();
        }
    }

    public void Toggle()
    {
        if (Visible) Hide();
        else Show();
    }

    public void Show()
    {
        Rebuild();
        PositionAndSize();
        _appWindow.Show(true);
        _window.Activate();
        Visible = true;
        _pinButton.Focus(FocusState.Programmatic);
        // Backdrops can silently fail on a never-shown window; re-apply now.
        _appliedBackdropTheme = null;
        ApplyBackdrop();
    }

    public void Hide()
    {
        _appWindow.Hide();
        Visible = false;
    }

    public void TogglePin()
    {
        _settings.IsPinned = !_settings.IsPinned;
        _presenter.IsAlwaysOnTop = _settings.IsPinned;
        _settings.Save();
        Rebuild();
        SettingsChanged?.Invoke();
    }

    private void ToggleTimeDisplayMode()
    {
        _settings.TimeDisplayMode = string.Equals(_settings.TimeDisplayMode, "RemainingTime", StringComparison.OrdinalIgnoreCase)
            ? "ClockTime"
            : "RemainingTime";
        _settings.Save();
        Rebuild();
        SettingsChanged?.Invoke();
    }

    private bool _dragging;
    private NativePoint _dragStartCursor;
    private PointInt32 _dragStartWindow;

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_headerGrid);
        if (!point.Properties.IsLeftButtonPressed) return;
        GetCursorPos(out _dragStartCursor);
        _dragStartWindow = _appWindow.Position;
        _dragging = true;
        _headerGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var cursor);
        _appWindow.Move(new PointInt32(
            _dragStartWindow.X + (cursor.X - _dragStartCursor.X),
            _dragStartWindow.Y + (cursor.Y - _dragStartCursor.Y)));
    }

    private void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _headerGrid.ReleasePointerCapture(e.Pointer);
    }

    // ----- content -----

    private string ResolveTheme()
    {
        if (_settings.FollowSystemTheme)
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Dark";
        }
        return _settings.ThemeOverride;
    }

    public void SetMenu(MenuFlyout menu)
    {
        menu.Opening += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        _rootBorder.ContextFlyout = menu;
    }

    private void Rebuild()
    {
        var theme = ResolveTheme();
        var palette = PopupPalette.FromSettings(theme, _settings.Glassmorphism);
        _rootBorder.RequestedTheme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Light
            : ElementTheme.Dark;
        ApplyBackdrop();
        _titleText.Text = Loc.T("Usage", "사용량");

        _rootBorder.Background = Brush(palette.Card);
        _rootBorder.BorderBrush = Brush(palette.Border);
        _titleText.Foreground = Brush(palette.Text);
        _headerDivider.Background = Brush(Color.FromArgb(118, palette.AccentBlue.R, palette.AccentBlue.G, palette.AccentBlue.B));
        _pinIcon.Foreground = _settings.IsPinned ? Brush(Colors.White) : Brush(palette.Muted);
        _pinButton.Background = _settings.IsPinned ? Brush(palette.AccentBlue) : new SolidColorBrush(Colors.Transparent);

        _sectionsPanel.Children.Clear();

        var sections = BuildSections();
        if (sections.Count == 0)
        {
            _sectionsPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Waiting for connection", "연결 대기 중"),
                FontFamily = UiFont,
                FontSize = 12,
                Foreground = Brush(palette.Muted),
                Margin = new Thickness(2, 4, 0, 40)
            });
            return;
        }

        var usesBento = string.Equals(_settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < sections.Count; i++)
        {
            var (usage, rows) = sections[i];
            if (i > 0)
            {
                _sectionsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush(Color.FromArgb(90, palette.Border.R, palette.Border.G, palette.Border.B)),
                    Margin = new Thickness(2, 8, 2, 8)
                });
            }

            _sectionsPanel.Children.Add(BuildSectionHeader(usage, palette));

            if (usesBento)
            {
                AddBentoRows(rows, palette);
            }
            else
            {
                foreach (var row in rows)
                {
                    _sectionsPanel.Children.Add(BuildBarRow(row, palette));
                }
            }
        }
    }

    private List<(ProviderUsage Usage, List<UsageRow> Rows)> BuildSections()
    {
        var sections = new List<(ProviderUsage, List<UsageRow>)>();
        foreach (var usage in _usages)
        {
            var provider = _settings.GetProvider(usage.ProviderId);
            sections.Add((usage, usage.Rows.Where(row => IsRowVisible(row, provider)).ToList()));
        }
        return sections;
    }

    private static bool IsRowVisible(UsageRow row, ProviderSettings provider)
    {
        if (row.IsPrimary) return true;
        return row.Window is not null ? provider.ShowSecondaryRows : provider.ShowCredits;
    }

    private Grid BuildSectionHeader(ProviderUsage usage, PopupPalette palette)
    {
        var header = new Grid { Margin = new Thickness(2, 2, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = usage.DisplayName,
            FontFamily = UiFont,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(palette.AccentBlue),
            VerticalAlignment = VerticalAlignment.Center
        };
        var status = new TextBlock
        {
            Text = StatusTextFor(usage),
            FontFamily = UiFont,
            FontSize = 11,
            Foreground = Brush(palette.Muted),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Opacity = usage.State == ProviderState.Stale ? 0.75 : 1.0
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(status, 1);
        header.Children.Add(name);
        header.Children.Add(status);
        return header;
    }

    // ProviderState-specific status line (spec: Stale shows last update, RateLimited shows wait).
    private static string StatusTextFor(ProviderUsage usage)
    {
        return usage.State switch
        {
            ProviderState.Stale => usage.StatusText,
            ProviderState.RateLimited => usage.StatusText,
            ProviderState.Unauthenticated => usage.StatusText,
            ProviderState.Unavailable => usage.StatusText,
            _ => usage.StatusText
        };
    }

    private FrameworkElement BuildBarRow(UsageRow row, PopupPalette palette)
    {
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = row.Label,
            FontFamily = UiFont,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(palette.Text),
            MinWidth = row.Label.Length > 6 ? 104 : 72
        };
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var right = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 12.5,
            Foreground = Brush(palette.Muted),
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(right, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        if (row.Window is { } window)
        {
            var used = RoundPercent(window);
            var display = DisplayPercent(window);
            right.Text = $"{display}%  {FormatWindowTime(window)}";
            right.Tapped += (_, _) => ToggleTimeDisplayMode();

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = display,
                Height = 8,
                Margin = new Thickness(0, 8, 0, 0),
                CornerRadius = new CornerRadius(4),
                Foreground = Brush(palette.AccentFor(used)),
                Background = Brush(palette.Track)
            };
            Grid.SetRow(bar, 1);
            Grid.SetColumn(bar, 0);
            Grid.SetColumnSpan(bar, 2);
            grid.Children.Add(bar);
        }
        else
        {
            right.Text = row.DetailText ?? "--";
        }

        return new Border
        {
            Background = Brush(palette.Row),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 0, 0, 12),
            Child = grid
        };
    }

    private void AddBentoRows(List<UsageRow> rows, PopupPalette palette)
    {
        for (var i = 0; i < rows.Count; i += 2)
        {
            var isPair = i + 1 < rows.Count;
            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            if (isPair)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var left = BuildBentoCard(rows[i], palette);
                var rightCard = BuildBentoCard(rows[i + 1], palette);
                Grid.SetColumn(left, 0);
                Grid.SetColumn(rightCard, 2);
                rowGrid.Children.Add(left);
                rowGrid.Children.Add(rightCard);
            }
            else
            {
                // Trailing odd card spans the full width so the grid has no hole.
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var card = BuildBentoCard(rows[i], palette);
                Grid.SetColumn(card, 0);
                rowGrid.Children.Add(card);
            }
            _sectionsPanel.Children.Add(rowGrid);
        }
    }

    private FrameworkElement BuildBentoCard(UsageRow row, PopupPalette palette)
    {
        var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = row.Label,
            FontFamily = UiFont,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(palette.Text)
        };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        if (row.Window is { } window)
        {
            var used = RoundPercent(window);
            var display = DisplayPercent(window);
            var ringGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            var ring = new ProgressRing
            {
                IsIndeterminate = false,
                Minimum = 0,
                Maximum = 100,
                Value = display,
                Width = 118,
                Height = 118,
                Foreground = Brush(palette.AccentFor(used)),
                Background = Brush(palette.Track)
            };
            var percentText = new TextBlock
            {
                Text = $"{display}%",
                FontFamily = UiFont,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(palette.Text),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ringGrid.Children.Add(ring);
            ringGrid.Children.Add(percentText);
            Grid.SetRow(ringGrid, 1);
            grid.Children.Add(ringGrid);

            var time = new TextBlock
            {
                Text = FormatWindowTime(window),
                FontFamily = UiFont,
                FontSize = 11.5,
                Foreground = Brush(palette.Muted),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            time.Tapped += (_, _) => ToggleTimeDisplayMode();
            Grid.SetRow(time, 2);
            grid.Children.Add(time);
        }
        else
        {
            var detail = new TextBlock
            {
                Text = row.DetailText ?? "--",
                FontFamily = UiFont,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(palette.Text),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(detail, 1);
            grid.Children.Add(detail);
        }

        return new Border
        {
            Height = BentoCardHeight,
            Background = Brush(palette.Row),
            BorderBrush = Brush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = grid
        };
    }

    private void ApplyBackdrop()
    {
        var isGlass = _settings.Glassmorphism;
        var key = isGlass ? "acrylic" : "mica";
        if (_appliedBackdropTheme == key) return;
        _appliedBackdropTheme = key;
        try
        {
            _window.SystemBackdrop = isGlass ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        }
        catch
        {
            _window.SystemBackdrop = null; // unsupported OS: opaque card still renders fine
        }
    }

    // ----- sizing & position -----

    private void PositionAndSize()
    {
        var sections = BuildSections();
        var usesBento = string.Equals(_settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase);
        var totalCards = sections.Sum(s => s.Rows.Count);
        var widthDip = usesBento
            ? (totalCards == 1 ? BentoSingleCardWidth : BentoWidth)
            : BarsWidth;
        var uiScale = Math.Clamp(_settings.UiScale, 0.7, 1.6);

        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        // Land on the target monitor first so GetDpiForWindow reports its scale.
        _appWindow.Move(new PointInt32(work.X + 8, work.Y + 8));
        var scale = GetDpiForWindow(_hwnd) / 96.0;

        // Measure content at the base width; the Viewbox stretches the fixed
        // base-size root to the (uiScale-adjusted) window.
        _rootBorder.Height = double.NaN;
        _rootBorder.Width = widthDip;
        _rootBorder.Measure(new global::Windows.Foundation.Size(widthDip, double.PositiveInfinity));
        var heightDip = Math.Ceiling(_rootBorder.DesiredSize.Height);
        _rootBorder.Height = heightDip;

        var width = (int)Math.Round(widthDip * uiScale * scale);
        var height = (int)Math.Round(heightDip * uiScale * scale);
        var margin = (int)Math.Round(16 * scale);

        var position = _settings.PopupPosition ?? "BottomRight";
        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;

        if (position.Equals("TopRight", StringComparison.OrdinalIgnoreCase))
        {
            x = work.X + work.Width - width - margin;
            y = work.Y + margin;
        }
        else if (position.Equals("TopLeft", StringComparison.OrdinalIgnoreCase))
        {
            x = work.X + margin;
            y = work.Y + margin;
        }
        else if (position.Equals("BottomLeft", StringComparison.OrdinalIgnoreCase))
        {
            x = work.X + margin;
            y = work.Y + work.Height - height - margin;
        }
        else if (position.Equals("Center", StringComparison.OrdinalIgnoreCase))
        {
            x = work.X + (work.Width - width) / 2;
            y = work.Y + (work.Height - height) / 2;
        }
        else if (position.Equals("NearCursor", StringComparison.OrdinalIgnoreCase))
        {
            x = cursor.X + (int)Math.Round(14 * scale);
            y = cursor.Y + (int)Math.Round(14 * scale);
        }

        x = Math.Min(Math.Max(x, work.X + margin), work.X + work.Width - width - margin);
        y = Math.Min(Math.Max(y, work.Y + margin), work.Y + work.Height - height - margin);

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    // ----- formatting (ported from UsagePopupForm) -----

    // Model is usedPercent; accent colors always key off usage.
    private static int RoundPercent(RateLimitWindow window)
    {
        return (int)Math.Round(Math.Clamp(window.UsedPercent, 0d, 100d));
    }

    // What gauges and percent texts show follows the configured metric.
    private int DisplayPercent(RateLimitWindow window)
    {
        var used = RoundPercent(window);
        return string.Equals(_settings.GaugeMetric, "Remaining", StringComparison.OrdinalIgnoreCase)
            ? 100 - used
            : used;
    }

    private string FormatWindowTime(RateLimitWindow? window)
    {
        if (window?.ResetsAt is null) return "reset --";
        if (string.Equals(_settings.TimeDisplayMode, "RemainingTime", StringComparison.OrdinalIgnoreCase))
        {
            return FormatRemaining(window.ResetsAt.Value);
        }

        var local = window.ResetsAt.Value.ToLocalTime().DateTime;
        return local.ToString("h:mm tt");
    }

    private static string FormatRemaining(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "0m";
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }
        return $"{Math.Max(1, remaining.Minutes)}m";
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    // ----- interop -----

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    private void ApplyWindowChrome()
    {
        // Windows 11 only; on Windows 10 these fail harmlessly.
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        // Remove the system's 1px window border line around the borderless popup.
        var none = unchecked((int)DwmwaColorNone);
        _ = DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
