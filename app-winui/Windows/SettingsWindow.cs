using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Func<IReadOnlyList<UsageRowRef>> _currentRows;
    private readonly NavigationView _nav;
    private bool _appliedKorean;

    public SettingsWindow(
        AppSettings settings,
        Action<SettingsChange> onChanged,
        Func<string> codexResolvedCommand,
        Action requestReconnect,
        IHotkeyConfigurator hotkeys,
        Func<IReadOnlyList<UsageRowRef>> currentRows)
    {
        _settings = settings;
        _onChanged = onChanged;
        _codexResolvedCommand = codexResolvedCommand;
        _requestReconnect = requestReconnect;
        _hotkeys = hotkeys;
        _currentRows = currentRows;
        _appliedKorean = Loc.IsKorean;

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
        _window.Title = Loc.T("QuotaScope Settings", "QuotaScope 설정");
        try
        {
            _window.SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
        }
        // Hide instead of destroying: sidesteps last-window lifetime policy and
        // any close-time teardown crashes, and keeps page state for reopening.
        _window.AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            _window.AppWindow.Hide();
        };

        var hwnd = WindowNative.GetWindowHandle(_window);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)(920 * scale);
        var height = (int)(660 * scale);
        _window.AppWindow.Resize(new SizeInt32(width, height));

        // Center on the display the cursor is on instead of the OS default top-left.
        GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        _window.AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2)));
    }

    public void Activate()
    {
        // Rebuild so reopened settings reflect external changes (e.g. pin
        // toggled from the popup) and the current language.
        RelocalizeIfNeeded();
        RefreshCurrentPage();
        _window.AppWindow.Show(true);
        _window.Activate();
    }

    private void RefreshCurrentPage()
    {
        if (_nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            _nav.Content = BuildPage(tag);
        }
    }

    // Language changes apply live: retitle the window and the navigation items,
    // then rebuild the visible page.
    private void RelocalizeIfNeeded()
    {
        if (_appliedKorean == Loc.IsKorean) return;
        _appliedKorean = Loc.IsKorean;
        _window.Title = Loc.T("QuotaScope Settings", "QuotaScope 설정");
        foreach (var menuItem in _nav.MenuItems)
        {
            if (menuItem is NavigationViewItem { Tag: string tag } navItem)
            {
                navItem.Content = NavItemText(tag);
            }
        }
    }

    private static string NavItemText(string tag) => tag switch
    {
        "General" => Loc.T("General", "일반"),
        "Providers" => Loc.T("Providers", "프로바이더"),
        "Appearance" => Loc.T("Appearance", "모양"),
        "Hotkeys" => Loc.T("Hotkeys", "단축키"),
        "About" => Loc.T("About", "정보"),
        _ => tag
    };

    private void AddNavItem(string tag, string glyph)
    {
        _nav.MenuItems.Add(new NavigationViewItem
        {
            Content = NavItemText(tag),
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
        // TrayController applies the language before anything else, so the
        // window can relocalize itself right after.
        _onChanged(kind);
        if (_appliedKorean != Loc.IsKorean)
        {
            RelocalizeIfNeeded();
            RefreshCurrentPage();
        }
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
            new[] { "BottomRight", "TopRight", "TopLeft", "BottomLeft", "Center", "NearCursor", "LastPosition" },
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

        var language = MakeCombo(
            new[] { "System", "English", "한국어" },
            _settings.Language,
            value => { _settings.Language = value; Save(SettingsChange.General); });

        var notify = new ToggleSwitch { IsOn = _settings.NotifyOnThreshold };
        notify.Toggled += (_, _) =>
        {
            _settings.NotifyOnThreshold = notify.IsOn;
            Save(SettingsChange.General);
        };

        return Page(Loc.T("General", "일반"),
            Row(Loc.T("Language", "언어"), Loc.T("Menus and settings text. Reopen this window to fully apply.", "메뉴와 설정 텍스트에 적용됩니다. 완전 적용은 이 창을 다시 여세요."), language),
            Row(Loc.T("Start with Windows", "Windows 시작 시 자동 실행"), Loc.T("Registers the app in the current user's Run key.", "현재 사용자 Run 레지스트리에 등록합니다."), autostart),
            Row(Loc.T("Popup position", "팝업 위치"), null, position),
            Row(Loc.T("Time display", "시간 표시"), Loc.T("Reset times as clock time or remaining time.", "리셋 시각을 시계 시간 또는 남은 시간으로 표시합니다."), timeDisplay),
            Row(Loc.T("Warning threshold", "경고 임계값"), Loc.T("Warn when remaining capacity drops to this percent.", "남은 용량이 이 퍼센트 이하로 떨어지면 경고합니다."), threshold),
            Row(Loc.T("Threshold notification", "임계값 알림"), Loc.T("Show a tray notification when usage crosses into warning or critical.", "사용량이 경고/위험 단계로 진입하면 트레이 알림을 표시합니다."), notify));
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
            ? Loc.T("Claude Code credentials found.", "Claude Code 자격증명을 찾았습니다.")
            : Loc.T("Not signed in. Run Claude Code and sign in.", "미로그인 상태입니다. Claude Code를 실행해 로그인하세요."));
        var reconnect = new Button { Content = Loc.T("Reconnect providers", "프로바이더 재연결") };
        reconnect.Click += (_, _) => _requestReconnect();

        var enabledLabel = Loc.T("Enabled", "사용");
        var refreshLabel = Loc.T("Refresh interval (seconds)", "갱신 주기 (초)");
        var creditsLabel = Loc.T("Credits row", "크레딧 행");
        return Page(Loc.T("Providers", "프로바이더"),
            SectionLabel("Codex"),
            Row(enabledLabel, Loc.T("Changes apply without restart.", "재시작 없이 반영됩니다."), ProviderToggle(codex, SettingsChange.Providers)),
            Row(refreshLabel, Loc.T("Minimum 10 seconds.", "최소 10초."), RefreshBox(codex, 10)),
            Row(Loc.T("GPT-5.3-Codex-Spark rows", "GPT-5.3-Codex-Spark 행"), null, SecondaryToggle(codex)),
            Row(creditsLabel, Loc.T("Shows the credits balance.", "크레딧 잔액을 표시합니다."), CreditsToggle(codex)),
            Row(Loc.T("Codex command", "Codex 명령"), Loc.T("Command or full path used to start codex app-server.", "codex app-server 실행에 쓰는 명령 또는 전체 경로."), codexCommand),
            codexResolved,
            SectionLabel("Claude"),
            Row(enabledLabel, null, ProviderToggle(claude, SettingsChange.Providers)),
            Row(refreshLabel, Loc.T("Clamped to a 60-second minimum.", "최소 60초로 제한됩니다."), RefreshBox(claude, 60)),
            Row(Loc.T("Per-model rows", "모델별 행"), Loc.T("7d Sonnet / 7d Opus windows.", "7d Sonnet / 7d Opus window."), SecondaryToggle(claude)),
            Row(creditsLabel, Loc.T("Extra usage, when enabled on the account.", "계정에 활성화된 경우 extra usage를 표시합니다."), CreditsToggle(claude)),
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
            new[] { "Bars", "BentoCircles", "MixMatch" },
            _settings.ShapeTheme,
            value =>
            {
                _settings.ShapeTheme = value;
                Save(SettingsChange.Appearance);
                // Show/hide the per-row list that only applies to mix & match.
                RefreshCurrentPage();
            });

        var themeCombo = MakeCombo(
            new[] { "Dark", "Light", "Midnight" },
            _settings.ThemeOverride,
            value => { _settings.ThemeOverride = value; ApplyTheme(); Save(SettingsChange.Appearance); });
        themeCombo.IsEnabled = !_settings.FollowSystemTheme;

        var followSystem = new ToggleSwitch { IsOn = _settings.FollowSystemTheme };
        followSystem.Toggled += (_, _) =>
        {
            _settings.FollowSystemTheme = followSystem.IsOn;
            themeCombo.IsEnabled = !followSystem.IsOn;
            ApplyTheme();
            Save(SettingsChange.Appearance);
        };

        var glass = new ToggleSwitch { IsOn = _settings.Glassmorphism };
        glass.Toggled += (_, _) =>
        {
            _settings.Glassmorphism = glass.IsOn;
            Save(SettingsChange.Appearance);
            RefreshCurrentPage(); // show/hide the strength picker
        };

        var glassStrength = MakeCombo(
            new[] { "Subtle", "Medium", "Strong", "VeryStrong" },
            _settings.GlassStrength,
            value => { _settings.GlassStrength = value; Save(SettingsChange.Appearance); });

        var trayStyle = MakeCombo(
            new[] { "UsageArc", "Glyph" },
            _settings.TrayIconStyle,
            value => { _settings.TrayIconStyle = value; Save(SettingsChange.Appearance); });

        var gaugeMetric = MakeCombo(
            new[] { "Used", "Remaining" },
            _settings.GaugeMetric,
            value => { _settings.GaugeMetric = value; Save(SettingsChange.Appearance); });

        var uiScale = MakeCombo(
            new[] { "80%", "90%", "100%", "110%", "125%", "150%" },
            $"{(int)Math.Round(Math.Clamp(_settings.UiScale, 0.7, 1.6) * 100)}%",
            value =>
            {
                if (int.TryParse(value.TrimEnd('%'), out var percent))
                {
                    _settings.UiScale = percent / 100.0;
                    Save(SettingsChange.Appearance);
                }
            });

        var rows = new List<FrameworkElement>
        {
            Row(Loc.T("Shape theme", "게이지 모양"),
                Loc.T("Bars, gauges, or mix & match per row.", "막대, 게이지, 또는 행별 믹스 & 매치."), shape)
        };
        if (string.Equals(_settings.ShapeTheme, "MixMatch", StringComparison.OrdinalIgnoreCase))
        {
            var columns = MakeCombo(
                new[] { "Auto", "OneColumn", "TwoColumns" },
                _settings.LayoutColumns,
                value => { _settings.LayoutColumns = value; Save(SettingsChange.Appearance); });
            rows.Add(Row(
                Loc.T("Columns", "열 수"),
                Loc.T("Auto uses two columns only when a provider has two gauges.",
                      "자동은 한 프로바이더에 게이지가 2개 이상일 때만 2열로 배치합니다."),
                columns));
            rows.Add(BuildMixMatchList());
        }
        rows.Add(Row(Loc.T("UI scale", "UI 크기"), Loc.T("Overall popup size.", "팝업 전체 크기를 조절합니다."), uiScale));
        rows.AddRange(new[]
        {
            Row(Loc.T("Follow system theme", "시스템 테마 따르기"), Loc.T("Light/dark follows Windows.", "Windows의 라이트/다크를 따릅니다."), followSystem),
            Row(Loc.T("Theme", "테마"), Loc.T("Dark, Light, or Midnight (pure black). Applies to popup and settings.", "다크, 라이트, 미드나잇(순수 검정). 팝업과 설정창에 적용됩니다."), themeCombo),
            Row(Loc.T("Glassmorphism", "글래스모피즘"), Loc.T("Translucent acrylic that shows the desktop behind the popup.", "팝업 뒤 배경이 비치는 아크릴 반투명 효과."), glass)
        });
        if (_settings.Glassmorphism)
        {
            rows.Add(Row(
                Loc.T("Glass strength", "글래스 강도"),
                Loc.T("Stronger lets more of the desktop through the popup and its cards.",
                      "강할수록 팝업과 카드 너머 배경이 더 많이 비칩니다."),
                glassStrength));
        }
        if (!AcrylicBackdropHost.IsSupported)
        {
            rows.Add(MutedText(Loc.T(
                "Acrylic is not supported on this system; glassmorphism falls back to a flat tint.",
                "이 시스템은 아크릴을 지원하지 않아 글래스모피즘이 단색으로 표시됩니다.")));
        }
        else if (!AcrylicBackdropHost.TransparencyEffectsEnabled)
        {
            rows.Add(MutedText(Loc.T(
                "Windows transparency effects are off, so glass renders as a flat color. Turn them on in Settings > Personalization > Colors.",
                "Windows 투명 효과가 꺼져 있어 글래스가 단색으로 표시됩니다. 설정 > 개인 설정 > 색에서 켜세요.")));
        }
        rows.AddRange(new[]
        {
            Row(Loc.T("Tray icon style", "트레이 아이콘 스타일"), Loc.T("Usage arc, or a plain dot carrying only the state color.", "사용률 호 또는 상태 색상만 담은 점."), trayStyle),
            Row(Loc.T("Gauge metric", "게이지 지표"), Loc.T("Whether gauges and percentages show used or remaining capacity.", "게이지와 퍼센트가 사용량/잔여량 중 무엇을 표시할지."), gaugeMetric)
        });
        return Page(Loc.T("Appearance", "모양"), rows.ToArray());
    }

    // Mix & match: one shape selector per known row. Bars always span the full
    // content width; gauges pair up two per line when at least one provider has
    // two of them, otherwise everything stacks in a single column.
    private FrameworkElement BuildMixMatchList()
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(16, 12, 16, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Shape per row", "행별 모양"),
            FontSize = 14
        });
        panel.Children.Add(MutedText(Loc.T(
            "Bars take the full popup width; gauges sit two per line when possible.",
            "막대는 팝업 전체 폭을 쓰고, 게이지는 가능하면 한 줄에 두 개씩 배치됩니다.")));

        var rows = _currentRows();
        if (rows.Count == 0)
        {
            panel.Children.Add(MutedText(Loc.T(
                "No rows yet. Open the popup once so providers report their rows.",
                "아직 표시할 행이 없습니다. 팝업을 한 번 열어 프로바이더 행을 받아오세요.")));
        }

        foreach (var rowRef in rows)
        {
            var key = RowShapes.Key(rowRef.ProviderId, rowRef.Label);
            var current = _settings.RowShapes.TryGetValue(key, out var stored) ? stored : RowShapes.Circle;
            var combo = MakeCombo(
                new[] { RowShapes.Circle, RowShapes.Bars },
                current,
                value =>
                {
                    _settings.RowShapes[key] = value;
                    Save(SettingsChange.Appearance);
                },
                width: 140);

            var line = new Grid { MinHeight = 40 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = $"{rowRef.ProviderName} · {Loc.RowLabel(rowRef.ProviderId, rowRef.Label)}",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            combo.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            Grid.SetColumn(combo, 1);
            line.Children.Add(label);
            line.Children.Add(combo);
            panel.Children.Add(line);
        }

        var card = new Border
        {
            Child = panel,
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
        var unboundNote = Loc.T("Unbound by default.", "기본값은 미지정입니다.");
        rows.Add(HotkeyRow(Loc.T("Toggle popup", "팝업 토글"), Loc.T("Default Ctrl+Alt+U.", "기본값 Ctrl+Alt+U."), HotkeyAction.TogglePopup, error));
        rows.Add(HotkeyRow(Loc.T("Refresh all", "전체 새로 고침"), unboundNote, HotkeyAction.RefreshAll, error));
        rows.Add(HotkeyRow(Loc.T("Toggle pin", "핀 토글"), unboundNote, HotkeyAction.TogglePin, error));
        rows.Add(error);
        rows.Add(MutedText(Loc.T(
            "Click a box and press the combination (at least one modifier). Backspace clears the binding. If registration fails, the previous binding is kept and not overwritten.",
            "입력란을 클릭하고 조합키를 누르세요 (수정키 1개 이상 필요). Backspace는 바인딩을 해제합니다. 등록에 실패하면 기존 바인딩이 유지되고 저장되지 않습니다.")));
        return Page(Loc.T("Hotkeys", "단축키"), rows.ToArray());
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

    // Values stay stable English keys in settings.json; only the display text
    // is localized.
    private static ComboBox MakeCombo(string[] values, string? current, Action<string> apply, double width = 180)
    {
        var combo = new ComboBox { Width = width };
        foreach (var value in values)
        {
            combo.Items.Add(new ComboBoxItem { Content = Loc.Option(value), Tag = value });
        }
        var index = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value })
            {
                apply(value);
            }
        };
        return combo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
