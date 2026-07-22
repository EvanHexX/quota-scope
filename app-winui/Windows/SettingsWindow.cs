using System;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuotaScope.Providers.Claude;
using QuotaScope.WinUI.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace QuotaScope.WinUI.Windows;

internal enum SettingsChange
{
    General,
    Providers,
    Appearance
}

// Dedicated settings window replacing the old nested tray menus.
// Immediate-apply: every control writes to AppSettings, saves, and notifies
// the TrayController; there is no OK/Cancel.
internal sealed class SettingsWindow
{
    private readonly Window _window = new();
    private readonly AppSettings _settings;
    private readonly Action<SettingsChange> _onChanged;
    private readonly Func<string> _codexResolvedCommand;
    private readonly Action _requestReconnect;
    private readonly IHotkeyConfigurator _hotkeys;
    private readonly NavigationView _nav;

    public event Action? Closed;

    public SettingsWindow(
        AppSettings settings,
        Action<SettingsChange> onChanged,
        Func<string> codexResolvedCommand,
        Action requestReconnect,
        IHotkeyConfigurator hotkeys)
    {
        _settings = settings;
        _onChanged = onChanged;
        _codexResolvedCommand = codexResolvedCommand;
        _requestReconnect = requestReconnect;
        _hotkeys = hotkeys;

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 200
        };
        AddNavItem("General", "");
        AddNavItem("Providers", "");
        AddNavItem("Appearance", "");
        AddNavItem("Hotkeys", "");
        AddNavItem("About", "");
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                _nav.Content = BuildPage(tag);
            }
        };
        _nav.SelectedItem = _nav.MenuItems[0];
        ApplyTheme();

        _window.Content = _nav;
        _window.Title = "QuotaScope Settings";
        try
        {
            _window.SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
        }
        _window.Closed += (_, _) => Closed?.Invoke();

        var hwnd = WindowNative.GetWindowHandle(_window);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _window.AppWindow.Resize(new SizeInt32((int)(920 * scale), (int)(660 * scale)));
    }

    public void Activate() => _window.Activate();

    private void AddNavItem(string tag, string glyph)
    {
        _nav.MenuItems.Add(new NavigationViewItem
        {
            Content = tag,
            Tag = tag,
            Icon = new FontIcon { Glyph = glyph }
        });
    }

    private void ApplyTheme()
    {
        _nav.RequestedTheme = _settings.FollowSystemTheme
            ? ElementTheme.Default
            : string.Equals(_settings.ThemeOverride, "Light", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Light
                : ElementTheme.Dark;
    }

    private void Save(SettingsChange kind)
    {
        _settings.Save();
        _onChanged(kind);
    }

    // ----- pages -----

    private UIElement BuildPage(string tag) => tag switch
    {
        "Providers" => BuildProvidersPage(),
        "Appearance" => BuildAppearancePage(),
        "Hotkeys" => BuildHotkeysPage(),
        "About" => BuildAboutPage(),
        _ => BuildGeneralPage()
    };

    private UIElement BuildGeneralPage()
    {
        var autostart = new ToggleSwitch { IsOn = Autostart.IsEnabled() };
        autostart.Toggled += (_, _) =>
        {
            if (!Autostart.TrySet(autostart.IsOn))
            {
                autostart.IsOn = !autostart.IsOn;
                return;
            }
            _settings.Autostart = autostart.IsOn;
            Save(SettingsChange.General);
        };

        var position = MakeCombo(
            new[] { "BottomRight", "TopRight", "TopLeft", "BottomLeft", "Center", "NearCursor" },
            _settings.PopupPosition,
            value => { _settings.PopupPosition = value; Save(SettingsChange.General); });

        var timeDisplay = MakeCombo(
            new[] { "ClockTime", "RemainingTime" },
            _settings.TimeDisplayMode,
            value => { _settings.TimeDisplayMode = value; Save(SettingsChange.General); });

        var threshold = new NumberBox
        {
            Minimum = 1,
            Maximum = 99,
            Value = _settings.WarningThresholdPercent,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 5,
            Width = 160
        };
        threshold.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(threshold.Value)) return;
            _settings.WarningThresholdPercent = (int)Math.Clamp(threshold.Value, 1, 99);
            Save(SettingsChange.General);
        };

        return Page("General",
            Row("Start with Windows", "Registers the app in the current user's Run key.", autostart),
            Row("Popup position", null, position),
            Row("Time display", "Reset times as clock time or remaining time.", timeDisplay),
            Row("Warning threshold", "Warn when remaining capacity drops to this percent.", threshold));
    }

    private UIElement BuildProvidersPage()
    {
        var codex = _settings.GetProvider("codex");
        var claude = _settings.GetProvider("claude");

        var codexCommand = new TextBox { Text = codex.Command, Width = 220 };
        var codexResolved = MutedText($"Resolved: {_codexResolvedCommand()}");
        void ApplyCodexCommand()
        {
            var value = string.IsNullOrWhiteSpace(codexCommand.Text) ? "codex" : codexCommand.Text.Trim();
            if (value == codex.Command) return;
            codex.Command = value;
            Save(SettingsChange.Providers);
            codexResolved.Text = $"Resolved: {_codexResolvedCommand()}";
        }
        codexCommand.LostFocus += (_, _) => ApplyCodexCommand();

        var claudeStatus = MutedText(ClaudeCredentialReader.CredentialsFileExists()
            ? "Claude Code credentials found."
            : "Not signed in. Run Claude Code and sign in.");
        var reconnect = new Button { Content = "Reconnect providers" };
        reconnect.Click += (_, _) => _requestReconnect();

        return Page("Providers",
            SectionLabel("Codex"),
            Row("Enabled", "Requires restart-free rebuild of provider connections.", ProviderToggle(codex, SettingsChange.Providers)),
            Row("Refresh interval (seconds)", "Minimum 10 seconds.", RefreshBox(codex, 10)),
            Row("GPT-5.3-Codex-Spark rows", null, SecondaryToggle(codex)),
            Row("Credits row", "Shows the credits balance.", CreditsToggle(codex)),
            Row("Codex command", "Command or full path used to start codex app-server.", codexCommand),
            codexResolved,
            SectionLabel("Claude"),
            Row("Enabled", null, ProviderToggle(claude, SettingsChange.Providers)),
            Row("Refresh interval (seconds)", "Clamped to a 60-second minimum.", RefreshBox(claude, 60)),
            Row("Per-model rows", "7d Sonnet / 7d Opus windows.", SecondaryToggle(claude)),
            Row("Credits row", "Extra usage, when enabled on the account.", CreditsToggle(claude)),
            claudeStatus,
            reconnect);
    }

    private ToggleSwitch ProviderToggle(ProviderSettings provider, SettingsChange kind)
    {
        var toggle = new ToggleSwitch { IsOn = provider.Enabled };
        toggle.Toggled += (_, _) => { provider.Enabled = toggle.IsOn; Save(kind); };
        return toggle;
    }

    private NumberBox RefreshBox(ProviderSettings provider, int minimum)
    {
        var box = new NumberBox
        {
            Minimum = minimum,
            Maximum = 3600,
            Value = Math.Max(minimum, provider.RefreshSeconds),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 30,
            Width = 160
        };
        box.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(box.Value)) return;
            provider.RefreshSeconds = (int)Math.Clamp(box.Value, minimum, 3600);
            Save(SettingsChange.Providers);
        };
        return box;
    }

    private ToggleSwitch SecondaryToggle(ProviderSettings provider)
    {
        var toggle = new ToggleSwitch { IsOn = provider.ShowSecondaryRows };
        toggle.Toggled += (_, _) => { provider.ShowSecondaryRows = toggle.IsOn; Save(SettingsChange.General); };
        return toggle;
    }

    private ToggleSwitch CreditsToggle(ProviderSettings provider)
    {
        var toggle = new ToggleSwitch { IsOn = provider.ShowCredits };
        toggle.Toggled += (_, _) => { provider.ShowCredits = toggle.IsOn; Save(SettingsChange.General); };
        return toggle;
    }

    private UIElement BuildAppearancePage()
    {
        var shape = MakeCombo(
            new[] { "Bars", "BentoCircles" },
            _settings.ShapeTheme,
            value => { _settings.ShapeTheme = value; Save(SettingsChange.Appearance); });

        var color = MakeCombo(
            new[] { "DarkBluePurple", "MidnightBlack", "Nebula", "Glassmorphism" },
            _settings.ColorTheme,
            value => { _settings.ColorTheme = value; Save(SettingsChange.Appearance); });

        var overrideCombo = MakeCombo(
            new[] { "Dark", "Light" },
            _settings.ThemeOverride,
            value => { _settings.ThemeOverride = value; ApplyTheme(); Save(SettingsChange.Appearance); });
        overrideCombo.IsEnabled = !_settings.FollowSystemTheme;

        var followSystem = new ToggleSwitch { IsOn = _settings.FollowSystemTheme };
        followSystem.Toggled += (_, _) =>
        {
            _settings.FollowSystemTheme = followSystem.IsOn;
            overrideCombo.IsEnabled = !followSystem.IsOn;
            ApplyTheme();
            Save(SettingsChange.Appearance);
        };

        return Page("Appearance",
            Row("Shape theme", "Bars or bento circle cards in the popup.", shape),
            Row("Color theme", null, color),
            Row("Follow system theme", "Applies to this settings window.", followSystem),
            Row("Theme override", "Used when not following the system theme.", overrideCombo));
    }

    private UIElement BuildHotkeysPage()
    {
        var error = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Hotkey not applied",
            IsOpen = false,
            IsClosable = true
        };

        var rows = new System.Collections.Generic.List<FrameworkElement>();
        if (_hotkeys.LoadWarnings.Count > 0)
        {
            rows.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                Title = "Hotkey load warnings",
                Message = string.Join("\n", _hotkeys.LoadWarnings),
                IsOpen = true,
                IsClosable = true
            });
        }
        rows.Add(HotkeyRow("Toggle popup", "Default Ctrl+Alt+U.", HotkeyAction.TogglePopup, error));
        rows.Add(HotkeyRow("Refresh all", "Unbound by default.", HotkeyAction.RefreshAll, error));
        rows.Add(HotkeyRow("Toggle pin", "Unbound by default.", HotkeyAction.TogglePin, error));
        rows.Add(error);
        rows.Add(MutedText("Click a box and press the combination (at least one modifier). Backspace clears the binding. If registration fails, the previous binding is kept and not overwritten."));
        return Page("Hotkeys", rows.ToArray());
    }

    private FrameworkElement HotkeyRow(string title, string description, HotkeyAction action, InfoBar error)
    {
        var box = new HotkeyCaptureBox();
        box.SetBinding(_hotkeys.CurrentBinding(action));
        box.Captured += definition =>
        {
            var failure = _hotkeys.TryBind(action, definition.Format());
            if (failure is null)
            {
                error.IsOpen = false;
                box.SetBinding(definition.Format());
            }
            else
            {
                box.SetBinding(_hotkeys.CurrentBinding(action));
                error.Message = failure;
                error.IsOpen = true;
            }
        };
        box.Cleared += () =>
        {
            _hotkeys.TryBind(action, "");
            error.IsOpen = false;
        };
        return Row(title, description, box);
    }

    private UIElement BuildAboutPage()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        var repoLink = new HyperlinkButton
        {
            Content = "github.com/EvanHexX/quota-scope",
            NavigateUri = new Uri("https://github.com/EvanHexX/quota-scope")
        };
        return Page("About",
            new TextBlock { Text = "QuotaScope", FontSize = 20, FontWeight = FontWeights.SemiBold },
            MutedText($"Version {version} (WinUI 3 preview)"),
            MutedText("This project is not affiliated with, endorsed by, or sponsored by OpenAI or Anthropic. Codex is a product/service of OpenAI. Claude is a product/service of Anthropic."),
            repoLink);
    }

    // ----- building blocks -----

    private static UIElement Page(string title, params FrameworkElement[] rows)
    {
        var panel = new StackPanel { Padding = new Thickness(28, 20, 28, 28), Spacing = 12, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel };
    }

    private static FrameworkElement Row(string title, string? description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (description is not null)
        {
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        }

        var grid = new Grid { Padding = new Thickness(16, 12, 16, 12), MinHeight = 56 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);

        var card = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1)
        };
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var bg) && bg is Brush bgBrush)
        {
            card.Background = bgBrush;
        }
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var stroke) && stroke is Brush strokeBrush)
        {
            card.BorderBrush = strokeBrush;
        }
        return card;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 12, 0, 0)
    };

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap
    };

    private static ComboBox MakeCombo(string[] values, string? current, Action<string> apply)
    {
        var combo = new ComboBox { Width = 180 };
        foreach (var value in values)
        {
            combo.Items.Add(value);
        }
        var index = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string value)
            {
                apply(value);
            }
        };
        return combo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
