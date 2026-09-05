using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaScope.Hotkeys;
using QuotaScope.Providers;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;
using QuotaScope.WinUI.Tray;
using QuotaScope.WinUI.Windows;

namespace QuotaScope.WinUI;

internal enum HotkeyAction
{
    TogglePopup = 1,
    RefreshAll = 2,
    TogglePin = 3
}

// Settings window talks to the hotkey subsystem through this seam.
internal interface IHotkeyConfigurator
{
    IReadOnlyList<string> LoadWarnings { get; }
    string CurrentBinding(HotkeyAction action);
    // Returns null on success; an error message when the binding was rejected
    // (parse failure or RegisterHotKey conflict). Failed bindings are not saved.
    string? TryBind(HotkeyAction action, string text);
}

// WinUI port of the WinForms TrayApplicationContext: owns providers, polling,
// the tray icon, and (from commit 2) the usage popup.
internal sealed class TrayController : IDisposable, IHotkeyConfigurator
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly List<IUsageProvider> _providers = new();
    private readonly Dictionary<string, ProviderUsage> _usages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITrayIcon _trayIcon;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<string> _hotkeyWarnings = new();
    private CodexUsageProvider? _codexProvider;
    private UsagePopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private Hotkeys.HotkeyWindow? _hotkeyWindow;
    private bool _refreshing;
    private Task _pollInFlight = Task.CompletedTask;
    private bool _disposed;

    public TrayController()
    {
        Loc.SetLanguage(_settings.Language);
        BuildProviders();

        _trayIcon = new TrayIconHost();
        _trayIcon.SetMenu(BuildTrayMenuItems());
        _trayIcon.SetTooltip("Checking usage");
        _trayIcon.SetIcon(TrayIconRenderer.CreateUsageIcon(0, TrayIconState.Normal, TrayIconRenderer.GetNativeIconSize()));
        _trayIcon.LeftClicked += TogglePopup;
        _trayIcon.Show();

        InitializeHotkeys();

        _timer = _dispatcherQueue.CreateTimer();
        _timer.Interval = ComputeTimerInterval();
        _timer.IsRepeating = true;
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(false);
        _timer.Start();
        _ = RefreshAsync();
    }

    private void BuildProviders()
    {
        var codexSettings = _settings.GetProvider("codex");
        if (codexSettings.Enabled)
        {
            _codexProvider = new CodexUsageProvider(codexSettings);
            _providers.Add(_codexProvider);
        }

        var claudeSettings = _settings.GetProvider("claude");
        if (claudeSettings.Enabled)
        {
            _providers.Add(new ClaudeUsageProvider(claudeSettings));
        }

        foreach (var provider in _providers)
        {
            _usages[provider.Id] = ProviderUsage.Offline(
                provider.Id, provider.DisplayName, $"Waiting for {provider.DisplayName} connection", ProviderState.Unavailable);
            provider.UsageUpdated += UpdateUsage;
        }
    }

    private TimeSpan ComputeTimerInterval()
    {
        var refreshSeconds = _providers.Count > 0
            ? _providers.Min(p => _settings.GetProvider(p.Id).RefreshSeconds)
            : 60;
        return TimeSpan.FromSeconds(Math.Max(10, refreshSeconds));
    }

    // Applied when provider enable/refresh settings change: tear down and recreate.
    private void RebuildProviders()
    {
        foreach (var provider in _providers)
        {
            provider.UsageUpdated -= UpdateUsage;
            provider.Dispose();
        }
        _providers.Clear();
        _usages.Clear();
        _codexProvider = null;

        BuildProviders();
        _timer.Interval = ComputeTimerInterval();
        _ = RefreshAsync();
    }

    // Native tray menu model (Win32 TrackPopupMenu inside TrayIconHost).
    private IReadOnlyList<TrayMenuItem> BuildTrayMenuItems()
    {
        return new[]
        {
            new TrayMenuItem(Loc.T("Refresh", "새로 고침"), () => _ = RefreshAsync()),
            new TrayMenuItem(Loc.T("Toggle popup", "팝업 토글"), TogglePopup),
            new TrayMenuItem(Loc.T("Reconnect", "재연결"), () => _ = ReconnectAsync()),
            TrayMenuItem.Separator,
            new TrayMenuItem(Loc.T("Settings...", "설정..."), OpenSettings),
            TrayMenuItem.Separator,
            new TrayMenuItem(Loc.T("Exit", "종료"), ExitApplication)
        };
    }

    // XAML flyout used as the popup's context menu.
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MakeItem(Loc.T("Refresh", "새로 고침"), async () => await RefreshAsync().ConfigureAwait(false)));
        menu.Items.Add(MakeItem(Loc.T("Toggle popup", "팝업 토글"), TogglePopup));
        menu.Items.Add(MakeItem(Loc.T("Reconnect", "재연결"), async () => await ReconnectAsync().ConfigureAwait(false)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem(Loc.T("Settings...", "설정..."), OpenSettings));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem(Loc.T("Exit", "종료"), ExitApplication));
        return menu;
    }

    private static MenuFlyoutItem MakeItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    public void TogglePopup()
    {
        var popup = GetPopup();
        if (popup.Visible)
        {
            popup.Hide();
            return;
        }
        popup.SetUsage(CurrentUsages());
        popup.Show();
    }

    private UsagePopupWindow GetPopup()
    {
        if (_popup is null)
        {
            _popup = new UsagePopupWindow(_settings, BuildMenu, RefreshAsync);
        }
        return _popup;
    }

    public void OpenSettings()
    {
        _settingsWindow ??= new SettingsWindow(
            _settings,
            OnSettingsChanged,
            () => _codexProvider?.ResolvedCommandText ?? "disabled",
            () => _ = ReconnectAsync(),
            this,
            CurrentRowRefs);
        _settingsWindow.Activate();
    }

    // ----- hotkeys -----

    public IReadOnlyList<string> LoadWarnings => _hotkeyWarnings;

    private void InitializeHotkeys()
    {
        try
        {
            _hotkeyWindow = new Hotkeys.HotkeyWindow();
        }
        catch (Exception ex)
        {
            _hotkeyWarnings.Add("Hotkeys unavailable: " + ex.Message);
            return;
        }

        _hotkeyWindow.Pressed += OnHotkeyPressed;
        RegisterFromSettings(HotkeyAction.TogglePopup, _settings.Hotkey, fallback: "Ctrl+Alt+U");
        RegisterFromSettings(HotkeyAction.RefreshAll, _settings.HotkeyRefreshAll, fallback: null);
        RegisterFromSettings(HotkeyAction.TogglePin, _settings.HotkeyTogglePin, fallback: null);

        if (_hotkeyWarnings.Count > 0)
        {
            // Surface load-time problems immediately, not only on the settings page.
            _trayIcon.ShowNotification("QuotaScope hotkeys", string.Join("\n", _hotkeyWarnings));
        }
    }

    private void OnHotkeyPressed(int id)
    {
        switch ((HotkeyAction)id)
        {
            case HotkeyAction.TogglePopup:
                TogglePopup();
                break;
            case HotkeyAction.RefreshAll:
                _ = RefreshAsync();
                break;
            case HotkeyAction.TogglePin:
                GetPopup().TogglePin();
                break;
        }
    }

    private void RegisterFromSettings(HotkeyAction action, string text, string? fallback)
    {
        if (_hotkeyWindow is null || string.IsNullOrWhiteSpace(text)) return;

        if (!HotkeyDefinition.TryParse(text, out var definition))
        {
            // Invalid persisted string: fall back to the default (or unbound) and warn once.
            if (fallback is not null && HotkeyDefinition.TryParse(fallback, out var fallbackDefinition))
            {
                _hotkeyWarnings.Add($"Invalid hotkey '{text}' for {action}; using default {fallback}.");
                SetBindingSetting(action, fallback);
                _settings.Save();
                if (!_hotkeyWindow.TryRegister((int)action, fallbackDefinition, out var fallbackError))
                {
                    _hotkeyWarnings.Add($"{action}: {fallbackError}");
                }
            }
            else
            {
                _hotkeyWarnings.Add($"Invalid hotkey '{text}' for {action}; left unbound.");
                SetBindingSetting(action, "");
                _settings.Save();
            }
            return;
        }

        if (!_hotkeyWindow.TryRegister((int)action, definition, out var error))
        {
            _hotkeyWarnings.Add($"{action}: {error}");
        }
    }

    private void SetBindingSetting(HotkeyAction action, string text)
    {
        switch (action)
        {
            case HotkeyAction.TogglePopup:
                _settings.Hotkey = text;
                break;
            case HotkeyAction.RefreshAll:
                _settings.HotkeyRefreshAll = text;
                break;
            case HotkeyAction.TogglePin:
                _settings.HotkeyTogglePin = text;
                break;
        }
    }

    public string CurrentBinding(HotkeyAction action) => action switch
    {
        HotkeyAction.RefreshAll => _settings.HotkeyRefreshAll,
        HotkeyAction.TogglePin => _settings.HotkeyTogglePin,
        _ => _settings.Hotkey
    };

    public string? TryBind(HotkeyAction action, string text)
    {
        if (_hotkeyWindow is null) return "Hotkeys are unavailable in this session.";

        if (string.IsNullOrWhiteSpace(text))
        {
            _hotkeyWindow.Unregister((int)action);
            SetBindingSetting(action, "");
            _settings.Save();
            return null;
        }

        if (!HotkeyDefinition.TryParse(text, out var definition))
        {
            return $"'{text}' is not a valid hotkey.";
        }

        var previous = CurrentBinding(action);
        if (!_hotkeyWindow.TryRegister((int)action, definition, out var error))
        {
            // Restore the previous binding and do not save the failed one.
            if (HotkeyDefinition.TryParse(previous, out var previousDefinition))
            {
                _hotkeyWindow.TryRegister((int)action, previousDefinition, out _);
            }
            return error;
        }

        SetBindingSetting(action, definition.Format());
        _settings.Save();
        return null;
    }

    private DispatcherQueueTimer? _rebuildDebounce;

    private void OnSettingsChanged(SettingsChange kind)
    {
        // Settings handlers run on UI-control events; a bug here must log,
        // not take the whole tray app down.
        try
        {
            Loc.SetLanguage(_settings.Language);
            if (kind == SettingsChange.Providers)
            {
                // Debounce: NumberBox spinners fire per click; rebuilding
                // providers (child process restart) per tick is wasteful.
                _rebuildDebounce ??= CreateRebuildDebounce();
                _rebuildDebounce.Stop();
                _rebuildDebounce.Start();
            }
            // Rebuild menus so language changes apply without restart.
            _trayIcon.SetMenu(BuildTrayMenuItems());
            _popup?.SetMenu(BuildMenu());
            _popup?.ApplySettings();
            ApplyTrayVisuals(CurrentUsages());
        }
        catch (Exception ex)
        {
            CrashLog.Write("settings-changed", ex);
        }
    }

    private DispatcherQueueTimer CreateRebuildDebounce()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                RebuildProviders();
            }
            catch (Exception ex)
            {
                CrashLog.Write("provider-rebuild", ex);
            }
        };
        return timer;
    }

    // A poll already running is the answer to a new request, so its task is
    // handed back rather than a completed one: a caller that waits on the
    // result (the popup's header refresh button) then waits for real work.
    private Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshing) return _pollInFlight;
        _pollInFlight = RefreshCoreAsync();
        return _pollInFlight;
    }

    private async Task RefreshCoreAsync()
    {
        _refreshing = true;
        try
        {
            // One provider's failure must not affect the others.
            foreach (var provider in _providers)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var usage = await provider.ReadAsync(cts.Token).ConfigureAwait(false);
                    UpdateUsage(usage);
                }
                catch (Exception ex)
                {
                    UpdateUsage(ProviderUsage.Offline(
                        provider.Id, provider.DisplayName, FormatConnectionError(provider, ex), ProviderState.Unavailable));
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Task ReconnectAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshing) return _pollInFlight;
        _pollInFlight = ReconnectCoreAsync();
        return _pollInFlight;
    }

    private async Task ReconnectCoreAsync()
    {
        // If Claude needs a sign-in, open the login terminal right away so the
        // user only has to complete the browser flow; polling resumes on its
        // own once Claude Code rewrites the credentials file.
        var claudeSettings = _settings.GetProvider("claude");
        var claudeNeedsLogin = claudeSettings.Enabled
            && (!ClaudeCredentialReader.CredentialsFileExists()
                || (_usages.TryGetValue("claude", out var claudeUsage) && claudeUsage.State == ProviderState.Unauthenticated));
        if (claudeNeedsLogin && ClaudeLoginLauncher.TryLaunch())
        {
            _trayIcon.ShowNotification(
                Loc.T("Claude sign-in", "Claude 로그인"),
                Loc.T("Complete the login in the opened terminal/browser. Usage resumes automatically.",
                      "열린 터미널/브라우저에서 로그인을 완료하세요. 사용량 표시는 자동으로 재개됩니다."));
        }

        _refreshing = true;
        try
        {
            foreach (var provider in _providers)
            {
                try
                {
                    UpdateUsage(ProviderUsage.Offline(
                        provider.Id, provider.DisplayName, $"Connecting to {provider.DisplayName}...", ProviderState.Unavailable));
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var usage = await provider.ReconnectAsync(cts.Token).ConfigureAwait(false);
                    UpdateUsage(usage);
                }
                catch (Exception ex)
                {
                    UpdateUsage(ProviderUsage.Offline(
                        provider.Id, provider.DisplayName, FormatConnectionError(provider, ex), ProviderState.Unavailable));
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string FormatConnectionError(IUsageProvider provider, Exception ex)
    {
        return ex is OperationCanceledException
            ? $"{provider.DisplayName} connection timed out. Use Reconnect."
            : $"{provider.DisplayName} connection required: " + ex.Message;
    }

    private void UpdateUsage(ProviderUsage usage)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => UpdateUsage(usage));
            return;
        }
        if (_disposed) return;

        _usages[usage.ProviderId] = usage;
        ApplyTrayVisuals(CurrentUsages());
    }

    private TrayIconState _lastIconState = TrayIconState.Normal;

    private void ApplyTrayVisuals(IReadOnlyList<ProviderUsage> usages)
    {
        var overallUsed = usages.Count > 0 ? usages.Max(u => u.OverallUsedPercent) : 0d;
        var anyRateLimited = usages.Any(u => u.State == ProviderState.RateLimited);
        var state = TrayIconRenderer.ComputeState(overallUsed, _settings.WarningThresholdPercent, anyRateLimited);

        // Notify once per escalation (Normal -> Warning -> Critical); recovery resets silently.
        if (_settings.NotifyOnThreshold && state > _lastIconState)
        {
            var title = state == TrayIconState.Critical
                ? Loc.T("Usage critical", "사용량 위험")
                : Loc.T("Usage warning", "사용량 경고");
            _trayIcon.ShowNotification(title, TruncateTrayText(BuildTrayText(usages)));
        }
        _lastIconState = state;
        var size = TrayIconRenderer.GetNativeIconSize();
        // The arc fill follows the configured gauge metric; state colors always key off usage.
        var fill = string.Equals(_settings.GaugeMetric, "Remaining", StringComparison.OrdinalIgnoreCase)
            ? 100d - overallUsed
            : overallUsed;
        var icon = string.Equals(_settings.TrayIconStyle, "Glyph", StringComparison.OrdinalIgnoreCase)
            ? TrayIconRenderer.CreateGlyphIcon(state, size)
            : TrayIconRenderer.CreateUsageIcon(fill, state, size);
        _trayIcon.SetTooltip(TruncateTrayText(BuildTrayText(usages)));
        _trayIcon.SetIcon(icon);
        _popup?.SetUsage(usages);
    }

    // Every row the enabled providers currently report, for the mix & match
    // shape picker (including rows hidden by row-visibility toggles).
    private IReadOnlyList<UsageRowRef> CurrentRowRefs()
    {
        return CurrentUsages()
            .SelectMany(usage => usage.Rows.Select(row =>
                new UsageRowRef(usage.ProviderId, usage.DisplayName, row.Label, row.IsPrimary)))
            .ToList();
    }

    private IReadOnlyList<ProviderUsage> CurrentUsages()
    {
        return _providers.Select(p => _usages[p.Id]).ToList();
    }

    private string BuildTrayText(IReadOnlyList<ProviderUsage> usages)
    {
        if (usages.Count == 0) return "No providers enabled";

        var parts = new List<string>();
        foreach (var usage in usages)
        {
            // Same rows in the same order the popup shows, so hiding or
            // reordering a row carries over to the tooltip.
            var rows = RowShapes.Order(
                _settings,
                usage.ProviderId,
                usage.Rows.Where(r => r.Window is not null
                    && !RowShapes.IsCreditsRow(r.Label)
                    && RowShapes.IsVisible(_settings, usage.ProviderId, r)),
                r => r.Label);
            parts.Add(rows.Count == 0
                ? $"{usage.DisplayName} --"
                : $"{usage.DisplayName} " + string.Join(" / ", rows.Select(r =>
                    $"{Loc.RowLabel(usage.ProviderId, r.Label)} {FormatRemaining(r.Window!)}")));
        }
        return string.Join("  |  ", parts);
    }

    private static string TruncateTrayText(string text)
    {
        // The Win32 tray tooltip limit is 127 characters.
        return text.Length <= 127 ? text : text[..127];
    }

    // The model is usedPercent; the tooltip reports what is left, so it is
    // spelled out ("left" / "남음") to keep it unambiguous next to the arc.
    private static string FormatRemaining(RateLimitWindow window)
    {
        var remaining = 100 - (int)Math.Round(Math.Clamp(window.UsedPercent, 0d, 100d));
        return Loc.T($"{remaining}% left", $"{remaining}% 남음");
    }

    private void ExitApplication()
    {
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _hotkeyWindow?.Dispose();
        _popup?.Hide();
        _trayIcon.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }
}
