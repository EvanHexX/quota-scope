using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaScope.Providers;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;
using QuotaScope.WinUI.Tray;
using QuotaScope.WinUI.Windows;

namespace QuotaScope.WinUI;

// WinUI port of the WinForms TrayApplicationContext: owns providers, polling,
// the tray icon, and (from commit 2) the usage popup.
internal sealed class TrayController : IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly List<IUsageProvider> _providers = new();
    private readonly Dictionary<string, ProviderUsage> _usages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITrayIcon _trayIcon;
    private readonly DispatcherQueueTimer _timer;
    private CodexUsageProvider? _codexProvider;
    private UsagePopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private bool _refreshing;
    private bool _disposed;

    public TrayController()
    {
        BuildProviders();

        _trayIcon = new TrayIconHost();
        _trayIcon.SetMenu(BuildMenu());
        _trayIcon.SetTooltip("Checking usage");
        _trayIcon.SetIcon(TrayIconRenderer.CreateUsageIcon(0, TrayIconState.Normal, TrayIconRenderer.GetNativeIconSize()));
        _trayIcon.LeftClicked += TogglePopup;
        _trayIcon.Show();

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

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MakeItem("Refresh", async () => await RefreshAsync().ConfigureAwait(false)));
        menu.Items.Add(MakeItem("Toggle", TogglePopup));
        menu.Items.Add(MakeItem("Reconnect", async () => await ReconnectAsync().ConfigureAwait(false)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem("Settings...", OpenSettings));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem("Exit", ExitApplication));
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
            _popup = new UsagePopupWindow(_settings, BuildMenu);
        }
        return _popup;
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                _settings,
                OnSettingsChanged,
                () => _codexProvider?.ResolvedCommandText ?? "disabled",
                () => _ = ReconnectAsync());
            _settingsWindow.Closed += () => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void OnSettingsChanged(SettingsChange kind)
    {
        if (kind == SettingsChange.Providers)
        {
            RebuildProviders();
        }
        _popup?.ApplySettings();
        ApplyTrayVisuals(CurrentUsages());
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
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

    private async Task ReconnectAsync()
    {
        if (_refreshing || _disposed) return;
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

    private void ApplyTrayVisuals(IReadOnlyList<ProviderUsage> usages)
    {
        var overall = usages.Count > 0 ? usages.Max(u => u.OverallUsedPercent) : 0d;
        var anyRateLimited = usages.Any(u => u.State == ProviderState.RateLimited);
        var state = TrayIconRenderer.ComputeState(overall, _settings.WarningThresholdPercent, anyRateLimited);
        _trayIcon.SetTooltip(TruncateTrayText(BuildTrayText(usages)));
        _trayIcon.SetIcon(TrayIconRenderer.CreateUsageIcon(overall, state, TrayIconRenderer.GetNativeIconSize()));
        _popup?.SetUsage(usages);
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
            var rows = usage.Rows.Where(r => r.IsPrimary && r.Window is not null).ToList();
            parts.Add(rows.Count == 0
                ? $"{usage.DisplayName} --"
                : $"{usage.DisplayName} " + string.Join(" / ", rows.Select(r => $"{r.Label} {FormatPercent(r.Window!)}")));
        }
        return string.Join("  |  ", parts);
    }

    private static string TruncateTrayText(string text)
    {
        // The Win32 tray tooltip limit is 127 characters.
        return text.Length <= 127 ? text : text[..127];
    }

    private static string FormatPercent(RateLimitWindow window)
    {
        return $"{(int)Math.Round(window.UsedPercent)}%";
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
        _popup?.Hide();
        _trayIcon.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }
}
