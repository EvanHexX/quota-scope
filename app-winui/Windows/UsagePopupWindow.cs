using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private readonly Grid _islandRoot;
    private readonly Grid _headerGrid;
    private readonly TextBlock _titleText;
    private readonly Button _refreshButton;
    private readonly FontIcon _refreshIcon;
    private readonly Storyboard _refreshSpin;
    private readonly Func<Task> _refreshRequested;
    private readonly Button _pinButton;
    private readonly FontIcon _pinIcon;
    private readonly Border _headerDivider;
    private readonly StackPanel _sectionsPanel;
    private IReadOnlyList<ProviderUsage> _usages = Array.Empty<ProviderUsage>();
    private string? _appliedBackdropTheme;
    private AcrylicBackdropHost _glass = null!;
    private bool _glassAcrylicActive;
    private bool _menuOpen;
    private bool _refreshInFlight;
    private bool _manuallyPositioned;
    private bool _resizePendingAfterDrag;

    public bool Visible { get; private set; }

    public event Action? SettingsChanged;

    public UsagePopupWindow(AppSettings settings, Func<MenuFlyout> menuFactory, Func<Task> refreshRequested)
    {
        _settings = settings;
        _refreshRequested = refreshRequested;

        _titleText = new TextBlock
        {
            Text = "Usage",
            FontFamily = UiFont,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _refreshIcon = new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            // Spun in place while a poll is in flight, so the origin has to
            // be the glyph's own centre rather than its top-left corner.
            RenderTransformOrigin = new global::Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform()
        };
        _refreshSpin = BuildSpinStoryboard((RotateTransform)_refreshIcon.RenderTransform);
        _refreshButton = new Button
        {
            Content = _refreshIcon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent)
        };
        _refreshButton.Click += (_, _) => RequestRefresh();

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
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_titleText, 0);
        Grid.SetColumn(_refreshButton, 1);
        Grid.SetColumn(_pinButton, 2);
        _headerGrid.Children.Add(_titleText);
        _headerGrid.Children.Add(_refreshButton);
        _headerGrid.Children.Add(_pinButton);
        _headerDivider = new Border { Height = 1, Margin = new Thickness(2, 6, 2, 10) };

        _sectionsPanel = new StackPanel();

        var layoutRoot = new StackPanel();
        layoutRoot.Children.Add(_headerGrid);
        layoutRoot.Children.Add(_headerDivider);
        layoutRoot.Children.Add(_sectionsPanel);

        _rootBorder = new Border
        {
            // DWM owns the popup's outer rounded geometry. Applying a second,
            // larger XAML radius exposes the square island background between
            // the two curves, which looks like a black plate behind the card.
            CornerRadius = new CornerRadius(0),
            // Do not draw a second outer stroke. A square XAML border clipped
            // by DWM leaves dark lines and broken pixels at rounded corners.
            BorderThickness = new Thickness(0),
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
        // Include the top padding as well as the visible header in the manual
        // drag region. The header buttons stay interactive and are excluded below.
        _rootBorder.PointerPressed += OnDragRegionPointerPressed;
        _rootBorder.PointerMoved += OnDragRegionPointerMoved;
        _rootBorder.PointerReleased += OnDragRegionPointerReleased;
        _rootBorder.PointerCaptureLost += OnDragRegionPointerCaptureLost;

        SetMenu(menuFactory());

        // Viewbox handles the UI-scale option: the root lays out at its base
        // size and is stretched to the (scaled) window, so content always
        // fills the window exactly — no white edges, no clipping.
        _scaleHost = new Viewbox { Stretch = Stretch.Fill, Child = _rootBorder };
        // The island root is painted opaquely (except in glass mode) so no
        // edge pixel can ever show the island's default light background.
        _islandRoot = new Grid();
        _islandRoot.Children.Add(_scaleHost);
        _window.Content = _islandRoot;
        _window.Title = "Usage";
        _hwnd = WindowNative.GetWindowHandle(_window);
        _glass = new AcrylicBackdropHost(_window);
        _appWindow = _window.AppWindow;
        // Known WinUI 3 issue (microsoft-ui-xaml #8947/#9621): hiding the title
        // bar via the presenter leaves a white pixel strip at the top. The
        // community-verified fix is extending content into the title bar area.
        try
        {
            _window.ExtendsContentIntoTitleBar = true;
        }
        catch
        {
        }
        _presenter = OverlappedPresenter.Create();
        // Keep the logical DWM border so Windows can retain its rounded corners
        // and shadow. The visible one-pixel stroke is synchronized to the card.
        _presenter.SetBorderAndTitleBar(true, false);
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
        InstallTopNcCalcSizeSubclass();
        ApplyWindowChrome();

        _window.Activated += (_, e) =>
        {
            // Something in the WinUI/backdrop stack can reset DWM attributes
            // around activation; re-applying is idempotent and cheap.
            ApplyWindowChrome();
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
            if (_dragging)
            {
                _resizePendingAfterDrag = true;
            }
            else
            {
                PositionAndSize(_manuallyPositioned);
            }
        }
    }

    public void ApplySettings()
    {
        _presenter.IsAlwaysOnTop = _settings.IsPinned;
        _manuallyPositioned = false;
        _resizePendingAfterDrag = false;
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
        _manuallyPositioned = false;
        _resizePendingAfterDrag = false;
        Rebuild();
        PositionAndSize();
        _appWindow.Show(true);
        _window.Activate();
        Visible = true;
        _pinButton.Focus(FocusState.Programmatic);
        // Backdrops can silently fail on a never-shown window; re-apply now.
        var shownTheme = ResolveTheme();
        _appliedBackdropTheme = null;
        ApplyBackdrop(PopupPalette.FromSettings(shownTheme, _settings.Glassmorphism, _settings.GlassStrength), shownTheme);
        ApplyWindowChrome();
    }

    public void Hide()
    {
        SaveLastPosition();
        _appWindow.Hide();
        Visible = false;
    }

    // Feeds the "Last position" popup placement mode.
    private void SaveLastPosition()
    {
        var position = _appWindow.Position;
        if (_settings.LastPopupX == position.X && _settings.LastPopupY == position.Y) return;
        _settings.LastPopupX = position.X;
        _settings.LastPopupY = position.Y;
        _settings.Save();
    }

    // Header shortcut for the "Refresh" menu item: one poll of every provider,
    // not the heavier Reconnect, which restarts child processes and can open a
    // Claude sign-in terminal. Both stay on the popup's context menu.
    private async void RequestRefresh()
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        try
        {
            // Start the poll before the animation. A spin that fails to start is
            // cosmetic; a poll skipped because it did would be a dead button.
            var poll = _refreshRequested();
            _refreshSpin.Begin();
            // The floor keeps a poll that resolves faster than the eye from
            // reading as a flicker rather than as feedback.
            await Task.WhenAll(poll, Task.Delay(TimeSpan.FromMilliseconds(450)));
        }
        catch (Exception ex)
        {
            // Per-provider failures already surface as offline rows; an unhandled
            // exception in an async void handler would take the app down.
            CrashLog.Write("popup-refresh", ex);
        }
        finally
        {
            if (_window.DispatcherQueue.HasThreadAccess) EndRefreshSpin();
            else _window.DispatcherQueue.TryEnqueue(EndRefreshSpin);
        }
    }

    private void EndRefreshSpin()
    {
        // Released before stopping the spin: a stalled animation is cosmetic,
        // a flag left set would disable the button for the rest of the session.
        _refreshInFlight = false;
        _refreshSpin.Stop();
    }

    private static Storyboard BuildSpinStoryboard(RotateTransform transform)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
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

    private void OnDragRegionPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_rootBorder);
        var dragRegionHeight = _rootBorder.Padding.Top + _headerGrid.Height;
        if (!point.Properties.IsLeftButtonPressed || point.Position.Y > dragRegionHeight) return;
        var source = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(source, _pinButton) || IsDescendantOf(source, _refreshButton)) return;
        GetCursorPos(out _dragStartCursor);
        _dragStartWindow = _appWindow.Position;
        _dragging = true;
        _manuallyPositioned = true;
        _rootBorder.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragRegionPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var cursor);
        _appWindow.Move(new PointInt32(
            _dragStartWindow.X + (cursor.X - _dragStartCursor.X),
            _dragStartWindow.Y + (cursor.Y - _dragStartCursor.Y)));
    }

    private void OnDragRegionPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _rootBorder.ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void OnDragRegionPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragging = false;
        if (_resizePendingAfterDrag)
        {
            _resizePendingAfterDrag = false;
            if (Visible)
            {
                PositionAndSize(preservePosition: true);
            }
        }
        SaveLastPosition();
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
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
        var palette = PopupPalette.FromSettings(theme, _settings.Glassmorphism, _settings.GlassStrength);
        _rootBorder.RequestedTheme = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Light
            : ElementTheme.Dark;
        ApplyBackdrop(palette, theme);
        _islandRoot.Background = _settings.Glassmorphism
            ? new SolidColorBrush(Colors.Transparent)
            : Brush(Color.FromArgb(255, palette.Card.R, palette.Card.G, palette.Card.B));
        _chromeBorderColor = Color.FromArgb(255, palette.Card.R, palette.Card.G, palette.Card.B);
        ApplyWindowChrome();
        _titleText.Text = Loc.T("Usage", "사용량");

        _rootBorder.Background = Brush(palette.Card);
        _titleText.Foreground = Brush(palette.Text);
        _headerDivider.Background = Brush(Color.FromArgb(118, palette.AccentBlue.R, palette.AccentBlue.G, palette.AccentBlue.B));
        _refreshIcon.Foreground = Brush(palette.Muted);
        ToolTipService.SetToolTip(_refreshButton, Loc.T("Refresh", "새로 고침"));
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

        var twoColumn = UsesTwoColumnLayout(sections);
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
            AddSectionRows(usage.ProviderId, rows, palette, twoColumn);
        }
    }

    // Two columns only pay off when some section actually has a pair of gauges
    // to place side by side; otherwise everything stacks in one column and bar
    // rows get the single-column width. The user can force either column count.
    private bool UsesTwoColumnLayout(List<(ProviderUsage Usage, List<UsageRow> Rows)> sections)
    {
        if (string.Equals(_settings.LayoutColumns, "OneColumn", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(_settings.LayoutColumns, "TwoColumns", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var (usage, rows) in sections)
        {
            var circles = 0;
            foreach (var row in rows)
            {
                if (RowShapes.Resolve(_settings, usage.ProviderId, row) == RowShapes.Circle && ++circles >= 2)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void AddSectionRows(string providerId, List<UsageRow> rows, PopupPalette palette, bool twoColumn)
    {
        // Consecutive gauges pair up; a bar row always spans the full content
        // width and flushes any pending gauge first.
        UsageRow? pendingCircle = null;

        void FlushPending()
        {
            if (pendingCircle is null) return;
            _sectionsPanel.Children.Add(BuildCardRow(BuildBentoCard(providerId, pendingCircle, palette), null, palette));
            pendingCircle = null;
        }

        foreach (var row in rows)
        {
            if (RowShapes.Resolve(_settings, providerId, row) == RowShapes.Bars)
            {
                FlushPending();
                _sectionsPanel.Children.Add(BuildBarRow(providerId, row, palette, stackTime: !twoColumn));
                continue;
            }

            if (!twoColumn)
            {
                _sectionsPanel.Children.Add(BuildCardRow(BuildBentoCard(providerId, row, palette), null, palette));
                continue;
            }

            if (pendingCircle is null)
            {
                pendingCircle = row;
                continue;
            }

            _sectionsPanel.Children.Add(BuildCardRow(
                BuildBentoCard(providerId, pendingCircle, palette), BuildBentoCard(providerId, row, palette), palette));
            pendingCircle = null;
        }

        FlushPending();
    }

    private static Grid BuildCardRow(FrameworkElement left, FrameworkElement? right, PopupPalette palette)
    {
        var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        rowGrid.Children.Add(left);
        if (right is not null)
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 2);
            rowGrid.Children.Add(right);
        }
        return rowGrid;
    }

    private List<(ProviderUsage Usage, List<UsageRow> Rows)> BuildSections()
    {
        var sections = new List<(ProviderUsage, List<UsageRow>)>();
        foreach (var usage in _usages)
        {
            var visible = usage.Rows.Where(row => RowShapes.IsVisible(_settings, usage.ProviderId, row));
            // User-defined order decides which gauges end up adjacent, and
            // therefore which ones pair into a two-column line.
            sections.Add((usage, RowShapes.Order(_settings, usage.ProviderId, visible, row => row.Label)));
        }
        return sections;
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

    private FrameworkElement BuildBarRow(string providerId, UsageRow row, PopupPalette palette, bool stackTime)
    {
        // Wide layout: label | reset time (stretches) | percent on one line.
        // Narrow layout: label | percent, with the reset time on its own line
        // underneath so nothing gets truncated.
        var grid = new Grid { Margin = stackTime ? new Thickness(12, 11, 12, 11) : new Thickness(12, 8, 12, 8) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (stackTime) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = stackTime ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
        });
        if (!stackTime) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var timeColumn = stackTime ? 0 : 1;
        var percentColumn = stackTime ? 1 : 2;
        var barSpan = stackTime ? 2 : 3;

        var labelText = Loc.RowLabel(providerId, row.Label);
        var label = new TextBlock
        {
            Text = labelText,
            FontFamily = UiFont,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(palette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var timeText = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 12.5,
            Foreground = Brush(palette.Muted),
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = stackTime ? new Thickness(0, 2, 0, 0) : new Thickness(10, 0, 10, 0)
        };
        Grid.SetRow(timeText, stackTime ? 1 : 0);
        Grid.SetColumn(timeText, timeColumn);
        if (stackTime) Grid.SetColumnSpan(timeText, 2);
        grid.Children.Add(timeText);

        var percentText = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Text),
            TextAlignment = TextAlignment.Right
        };
        Grid.SetRow(percentText, 0);
        Grid.SetColumn(percentText, percentColumn);
        grid.Children.Add(percentText);

        if (row.Window is { } window)
        {
            var used = RoundPercent(window);
            var display = DisplayPercent(window);
            timeText.Text = FormatWindowTime(window);
            timeText.Tapped += (_, _) => ToggleTimeDisplayMode();
            percentText.Text = $"{display}%";

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
            Grid.SetRow(bar, stackTime ? 2 : 1);
            Grid.SetColumn(bar, 0);
            Grid.SetColumnSpan(bar, barSpan);
            grid.Children.Add(bar);
        }
        else
        {
            percentText.Text = row.DetailText ?? "--";
        }

        return new Border
        {
            Background = CardFill(palette),
            BorderBrush = CardStroke(palette),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 0, 0, 12),
            Child = grid
        };
    }

    // In glass mode the panes get a soft top-down sheen plus a bright rim, so
    // the cards themselves read as glass instead of flat film over the backdrop.
    private Brush CardFill(PopupPalette palette)
    {
        if (!_settings.Glassmorphism) return Brush(palette.Row);

        var strength = GlassStrength.Parse(_settings.GlassStrength);
        // Only the top sliver is lightened, and only slightly: a 15% pull toward
        // white with a small alpha bump. Anything stronger looks like a painted
        // gradient on a flat surface.
        const double sheenMix = 0.15;
        var top = Color.FromArgb(
            (byte)Math.Min(255, palette.Row.A + strength.SheenAlpha),
            Lighten(palette.Row.R, sheenMix),
            Lighten(palette.Row.G, sheenMix),
            Lighten(palette.Row.B, sheenMix));

        var gradient = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(0, 1)
        };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = top });
        gradient.GradientStops.Add(new GradientStop { Offset = 0.45, Color = palette.Row });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = palette.Row });
        return gradient;
    }

    private static byte Lighten(byte channel, double amount)
    {
        return (byte)Math.Clamp(channel + (255 - channel) * amount, 0, 255);
    }

    private Brush CardStroke(PopupPalette palette)
    {
        if (!_settings.Glassmorphism) return Brush(palette.Border);
        var isDark = !string.Equals(ResolveTheme(), "Light", StringComparison.OrdinalIgnoreCase);
        return Brush(palette.GlassEdge(isDark, GlassStrength.Parse(_settings.GlassStrength)));
    }

    private FrameworkElement BuildBentoCard(string providerId, UsageRow row, PopupPalette palette)
    {
        var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Loc.RowLabel(providerId, row.Label),
            FontFamily = UiFont,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(palette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis
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
            Background = CardFill(palette),
            BorderBrush = CardStroke(palette),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = grid
        };
    }

    private void ApplyBackdrop(PopupPalette palette, string theme)
    {
        var isGlass = _settings.Glassmorphism;
        var isDark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        // Tint/luminosity depend on palette and strength, so re-apply on change.
        var key = $"{(isGlass ? "acrylic" : "mica")}:{theme}:{_settings.GlassStrength}";
        if (_appliedBackdropTheme == key) return;
        _appliedBackdropTheme = key;
        try
        {
            if (isGlass)
            {
                // Manual controller: the XAML backdrop element gives no tint or
                // luminosity control, which made glass look like a color shift.
                _window.SystemBackdrop = null;
                _glassAcrylicActive = _glass.TryAttach(
                    Color.FromArgb(255, palette.Card.R, palette.Card.G, palette.Card.B),
                    isDark,
                    GlassStrength.Parse(_settings.GlassStrength));
                if (!_glassAcrylicActive)
                {
                    _window.SystemBackdrop = new DesktopAcrylicBackdrop();
                }
            }
            else
            {
                _glass.Detach();
                _glassAcrylicActive = false;
                _window.SystemBackdrop = new MicaBackdrop();
            }
        }
        catch
        {
            _glass.Detach();
            _glassAcrylicActive = false;
            _window.SystemBackdrop = null; // unsupported OS: opaque card still renders fine
        }
    }

    // ----- sizing & position -----

    private void PositionAndSize(bool preservePosition = false)
    {
        var sections = BuildSections();
        var totalRows = sections.Sum(s => s.Rows.Count);
        var twoColumn = UsesTwoColumnLayout(sections);
        // A forced single column is meant to be a narrow stack: match the width
        // of one card in the two-column layout.
        var forcedOneColumn = string.Equals(_settings.LayoutColumns, "OneColumn", StringComparison.OrdinalIgnoreCase);
        var singleGaugeOnly = totalRows == 1
            && !string.Equals(_settings.LayoutColumns, "TwoColumns", StringComparison.OrdinalIgnoreCase)
            && sections.All(s => s.Rows.All(r => RowShapes.Resolve(_settings, s.Usage.ProviderId, r) == RowShapes.Circle));
        // Two columns need the wide popup; a forced single column or a lone
        // gauge gets the compact width; anything else uses one-column width.
        var widthDip = twoColumn
            ? BentoWidth
            : forcedOneColumn || singleGaugeOnly ? BentoSingleCardWidth : BarsWidth;
        var uiScale = Math.Clamp(_settings.UiScale, 0.7, 1.6);

        var configuredPosition = _settings.PopupPosition ?? "BottomRight";
        // A visible popup must not chase the cursor on every refresh; re-anchor
        // to the cursor only while showing it.
        var keepPlace = preservePosition
            || (Visible && configuredPosition.Equals("NearCursor", StringComparison.OrdinalIgnoreCase));

        var currentPosition = _appWindow.Position;
        GetCursorPos(out var cursor);
        var areaAnchor = keepPlace
            ? new PointInt32(currentPosition.X, currentPosition.Y)
            : new PointInt32(cursor.X, cursor.Y);
        var area = DisplayArea.GetFromPoint(areaAnchor, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        // Land on the target monitor first so GetDpiForWindow reports its scale.
        // Skipped while visible: the intermediate move is a visible flicker.
        if (!keepPlace && !Visible)
        {
            _appWindow.Move(new PointInt32(work.X + 8, work.Y + 8));
        }
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

        int x;
        int y;
        if (keepPlace)
        {
            x = Math.Min(Math.Max(currentPosition.X, work.X), Math.Max(work.X, work.X + work.Width - width));
            y = Math.Min(Math.Max(currentPosition.Y, work.Y), Math.Max(work.Y, work.Y + work.Height - height));
        }
        else
        {
            var position = configuredPosition;
            x = work.X + work.Width - width - margin;
            y = work.Y + work.Height - height - margin;

            if (position.Equals("LastPosition", StringComparison.OrdinalIgnoreCase)
                && _settings.LastPopupX != int.MinValue && _settings.LastPopupY != int.MinValue)
            {
                // Re-resolve the work area around the remembered spot so the
                // popup returns to the monitor it was last used on.
                var lastArea = DisplayArea.GetFromPoint(
                    new PointInt32(_settings.LastPopupX, _settings.LastPopupY), DisplayAreaFallback.Nearest);
                work = lastArea.WorkArea;
                x = _settings.LastPopupX;
                y = _settings.LastPopupY;
            }
            else if (position.Equals("TopRight", StringComparison.OrdinalIgnoreCase))
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
        }

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
        if (window?.ResetsAt is null) return Loc.T("reset --", "재설정 --");
        return string.Equals(_settings.TimeDisplayMode, "RemainingTime", StringComparison.OrdinalIgnoreCase)
            ? Loc.ResetIn(window.ResetsAt.Value - DateTimeOffset.Now)
            : Loc.ResetClock(window.ResetsAt.Value.ToLocalTime());
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    // ----- interop -----

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaBorderColor = 34;

    private bool _chromeFailureLogged;
    private Color _chromeBorderColor = Color.FromArgb(255, 24, 26, 46);

    private void ApplyWindowChrome()
    {
        // Windows 11 only; on Windows 10 these fail harmlessly.
        var preference = DwmwcpRound;
        var cornerResult = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        // This OS build still composites a one-pixel visible frame when
        // DWMWA_COLOR_NONE is requested. Painting that frame in the current
        // card color absorbs the top row while retaining DWM rounding/shadow.
        var borderColor = _chromeBorderColor.R
            | (_chromeBorderColor.G << 8)
            | (_chromeBorderColor.B << 16);
        var borderResult = DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
        if ((cornerResult != 0 || borderResult != 0) && !_chromeFailureLogged)
        {
            _chromeFailureLogged = true;
            CrashLog.Write("window-chrome", new InvalidOperationException(
                $"DwmSetWindowAttribute results: corner=0x{cornerResult:X8}, border=0x{borderResult:X8}"));
        }
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

    // WinUI can leave a one-pixel non-client strip after hiding the title bar.
    // Preserve the default side/bottom frame calculation for DWM rounding and
    // shadow, but reclaim only the top inset as client content.
    private const uint WmNcCalcSize = 0x0083;
    private const uint SwpFrameChangedFlags = 0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/
        | 0x0004 /*NOZORDER*/ | 0x0010 /*NOACTIVATE*/ | 0x0020 /*FRAMECHANGED*/;
    private SubclassProc? _subclassProc;

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData);

    private void InstallTopNcCalcSizeSubclass()
    {
        _subclassProc = OnSubclassMessage; // rooted for the window's lifetime
        SetWindowSubclass(_hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChangedFlags);
    }

    private static IntPtr OnSubclassMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData)
    {
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            // NCCALCSIZE_PARAMS begins with rgrc[0], whose second int is RECT.top.
            var proposedTop = Marshal.ReadInt32(lParam, sizeof(int));
            var result = DefSubclassProc(hwnd, msg, wParam, lParam);
            Marshal.WriteInt32(lParam, sizeof(int), proposedTop);
            return result;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, IntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);
}
