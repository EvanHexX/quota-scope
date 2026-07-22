using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QuotaScope.Providers;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;

namespace QuotaScope;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _notifyIcon;
    private UsagePopupForm _popup;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<IUsageProvider> _providers = new();
    private readonly Dictionary<string, ProviderUsage> _usages = new(StringComparer.OrdinalIgnoreCase);
    private CodexUsageProvider? _codexProvider;
    private readonly HotkeyWindow _hotkeyWindow;
    private bool _refreshing;

    public TrayApplicationContext()
    {
        _popup = CreatePopup();

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

        _notifyIcon = new NotifyIcon
        {
            Text = "Checking usage",
            Icon = TrayIconRenderer.CreateUsageIcon(0, TrayIconState.Normal, TrayIconRenderer.GetNativeIconSize()),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _popup.ContextMenuStrip = _notifyIcon.ContextMenuStrip;
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };

        _hotkeyWindow = new HotkeyWindow(TogglePopup);
        _hotkeyWindow.Register();

        var refreshSeconds = _providers.Count > 0
            ? _providers.Min(p => _settings.GetProvider(p.Id).RefreshSeconds)
            : 60;
        _timer.Interval = Math.Max(10, refreshSeconds) * 1000;
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(false);
        _timer.Start();
        _ = RefreshAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, async (_, _) => await RefreshAsync().ConfigureAwait(false));
        menu.Items.Add("Toggle", null, (_, _) => TogglePopup());

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(BuildConnectionsMenu());
        settings.DropDownItems.Add(BuildPositionMenu());
        settings.DropDownItems.Add(BuildTimeDisplayMenu());
        settings.DropDownItems.Add(BuildUsageRowsMenu());
        settings.DropDownItems.Add(BuildShapeThemeMenu());
        settings.DropDownItems.Add(BuildColorThemeMenu());
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private ToolStripMenuItem BuildConnectionsMenu()
    {
        var item = new ToolStripMenuItem("Connections");
        item.DropDownItems.Add("Reconnect", null, async (_, _) => await ReconnectAsync().ConfigureAwait(false));
        item.DropDownItems.Add(new ToolStripSeparator());
        var commandText = _codexProvider is null ? "disabled" : _codexProvider.ResolvedCommandText;
        item.DropDownItems.Add(new ToolStripMenuItem($"Codex: {commandText}") { Enabled = false });
        var claudeStatus = !_settings.GetProvider("claude").Enabled
            ? "disabled"
            : ClaudeCredentialReader.CredentialsFileExists() ? "credentials found" : "not signed in";
        item.DropDownItems.Add(new ToolStripMenuItem($"Claude: {claudeStatus}") { Enabled = false });
        return item;
    }

    private ToolStripMenuItem BuildPositionMenu()
    {
        var item = new ToolStripMenuItem("Position");
        AddPositionItem(item, "Bottom Right", "BottomRight");
        AddPositionItem(item, "Top Right", "TopRight");
        AddPositionItem(item, "Top Left", "TopLeft");
        AddPositionItem(item, "Bottom Left", "BottomLeft");
        AddPositionItem(item, "Center", "Center");
        AddPositionItem(item, "Near Cursor", "NearCursor");
        return item;
    }

    private void AddPositionItem(ToolStripMenuItem parent, string label, string value)
    {
        var item = new ToolStripMenuItem(label)
        {
            Checked = string.Equals(_settings.PopupPosition, value, StringComparison.OrdinalIgnoreCase),
            CheckOnClick = false
        };
        item.Click += (_, _) =>
        {
            _settings.PopupPosition = value;
            _settings.Save();
            RefreshMenu();
            if (_popup.Visible)
            {
                PositionPopup(_popup);
            }
        };
        parent.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildTimeDisplayMenu()
    {
        var item = new ToolStripMenuItem("Time Display");
        AddSettingPreviewItem(item, "Clock Time", "TimeDisplayMode", "ClockTime");
        AddSettingPreviewItem(item, "Remaining Time", "TimeDisplayMode", "RemainingTime");
        return item;
    }

    private ToolStripMenuItem BuildUsageRowsMenu()
    {
        var item = new ToolStripMenuItem("Usage Rows");
        var codexSettings = _settings.GetProvider("codex");
        AddUsageRowToggle(item, "GPT-5.3 Spark", codexSettings.ShowSecondaryRows,
            () => codexSettings.ShowSecondaryRows = !codexSettings.ShowSecondaryRows);
        AddUsageRowToggle(item, "Credits", codexSettings.ShowCredits,
            () => codexSettings.ShowCredits = !codexSettings.ShowCredits);
        return item;
    }

    private void AddUsageRowToggle(ToolStripMenuItem parent, string label, bool isChecked, Action toggle)
    {
        var item = new ToolStripMenuItem(label)
        {
            Checked = isChecked,
            CheckOnClick = false
        };
        item.Click += (_, _) =>
        {
            toggle();
            _settings.Save();
            var popup = GetPopup();
            popup.ApplySettings(_settings);
            RefreshMenu();
            if (popup.Visible)
            {
                PositionPopup(popup);
            }
        };
        parent.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildShapeThemeMenu()
    {
        var item = new ToolStripMenuItem("Shape Theme");
        AddSettingPreviewItem(item, "Bars", "ShapeTheme", "Bars");
        AddSettingPreviewItem(item, "Bento Circles", "ShapeTheme", "BentoCircles");
        return item;
    }

    private ToolStripMenuItem BuildColorThemeMenu()
    {
        var item = new ToolStripMenuItem("Color Theme");
        AddSettingPreviewItem(item, "DarkBluePurple", "ColorTheme", "DarkBluePurple");
        AddSettingPreviewItem(item, "MidnightBlack", "ColorTheme", "MidnightBlack");
        AddSettingPreviewItem(item, "Nebula", "ColorTheme", "Nebula");
        AddSettingPreviewItem(item, "Glassmorphism", "ColorTheme", "Glassmorphism");
        return item;
    }

    private void AddSettingPreviewItem(ToolStripMenuItem parent, string label, string property, string value)
    {
        var current = property == "ShapeTheme" ? _settings.ShapeTheme : property == "TimeDisplayMode" ? _settings.TimeDisplayMode : _settings.ColorTheme;
        var item = new ToolStripMenuItem(label)
        {
            Checked = string.Equals(current, value, StringComparison.OrdinalIgnoreCase),
            CheckOnClick = false
        };
        item.Click += (_, _) =>
        {
            if (property == "ShapeTheme")
            {
                _settings.ShapeTheme = value;
            }
            else if (property == "TimeDisplayMode")
            {
                _settings.TimeDisplayMode = value;
            }
            else
            {
                _settings.ColorTheme = value;
            }
            _settings.Save();
            GetPopup().ApplySettings(_settings);
            RefreshMenu();
        };
        parent.DropDownItems.Add(item);
    }

    private void RefreshMenu()
    {
        var menu = BuildMenu();
        _notifyIcon.ContextMenuStrip = menu;
        if (!_popup.IsDisposed)
        {
            _popup.ContextMenuStrip = menu;
        }
    }

    private UsagePopupForm CreatePopup()
    {
        var popup = new UsagePopupForm(_settings);
        popup.SettingsChanged += RefreshMenu;
        return popup;
    }

    private UsagePopupForm GetPopup()
    {
        if (_popup.IsDisposed)
        {
            _popup = CreatePopup();
            _popup.SetUsage(CurrentUsages());
        }
        _popup.ContextMenuStrip = _notifyIcon.ContextMenuStrip;
        _popup.ApplySettings(_settings);
        return _popup;
    }

    private IReadOnlyList<ProviderUsage> CurrentUsages()
    {
        return _providers.Select(p => _usages[p.Id]).ToList();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
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
        if (_refreshing) return;
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
            ? $"{provider.DisplayName} connection timed out. Use Settings > Connections > Reconnect."
            : $"{provider.DisplayName} connection required: " + ex.Message;
    }

    private void UpdateUsage(ProviderUsage usage)
    {
        if (Application.MessageLoop && !_popup.IsDisposed && _popup.InvokeRequired)
        {
            _popup.BeginInvoke(new Action(() => UpdateUsage(usage)));
            return;
        }

        var popup = GetPopup();
        _usages[usage.ProviderId] = usage;
        var usages = CurrentUsages();

        var overall = usages.Count > 0 ? usages.Max(u => u.OverallUsedPercent) : 0d;
        var anyRateLimited = usages.Any(u => u.State == ProviderState.RateLimited);
        var state = TrayIconRenderer.ComputeState(overall, _settings.WarningThresholdPercent, anyRateLimited);
        _notifyIcon.Text = TruncateTrayText(BuildTrayText(usages));
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconRenderer.CreateUsageIcon(overall, state, TrayIconRenderer.GetNativeIconSize());
        oldIcon?.Dispose();
        popup.SetUsage(usages);
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
        // NotifyIcon.Text throws over 127 characters.
        return text.Length <= 127 ? text : text[..127];
    }

    private static string FormatPercent(RateLimitWindow window)
    {
        return $"{(int)Math.Round(window.UsedPercent)}%";
    }

    private void TogglePopup()
    {
        var popup = GetPopup();
        if (popup.Visible)
        {
            popup.Hide();
            return;
        }

        popup.SetUsage(CurrentUsages());
        PositionPopup(popup);
        try
        {
            popup.Show();
            popup.Activate();
        }
        catch (InvalidOperationException)
        {
            _popup = CreatePopup();
            _popup.ContextMenuStrip = _notifyIcon.ContextMenuStrip;
            _popup.SetUsage(CurrentUsages());
            PositionPopup(_popup);
            _popup.Show();
            _popup.Activate();
        }
    }

    private void PositionPopup(Form popup)
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        const int margin = 16;
        var position = _settings.PopupPosition ?? "BottomRight";
        var x = screen.Right - popup.Width - margin;
        var y = screen.Bottom - popup.Height - margin;

        if (position.Equals("TopRight", StringComparison.OrdinalIgnoreCase))
        {
            x = screen.Right - popup.Width - margin;
            y = screen.Top + margin;
        }
        else if (position.Equals("TopLeft", StringComparison.OrdinalIgnoreCase))
        {
            x = screen.Left + margin;
            y = screen.Top + margin;
        }
        else if (position.Equals("BottomLeft", StringComparison.OrdinalIgnoreCase))
        {
            x = screen.Left + margin;
            y = screen.Bottom - popup.Height - margin;
        }
        else if (position.Equals("Center", StringComparison.OrdinalIgnoreCase))
        {
            x = screen.Left + (screen.Width - popup.Width) / 2;
            y = screen.Top + (screen.Height - popup.Height) / 2;
        }
        else if (position.Equals("NearCursor", StringComparison.OrdinalIgnoreCase))
        {
            x = Cursor.Position.X + 14;
            y = Cursor.Position.Y + 14;
        }

        popup.StartPosition = FormStartPosition.Manual;
        popup.Location = ClampToScreen(new Point(x, y), popup.Size, screen, margin);
    }

    private static Point ClampToScreen(Point point, Size size, Rectangle screen, int margin)
    {
        var x = Math.Min(Math.Max(point.X, screen.Left + margin), screen.Right - size.Width - margin);
        var y = Math.Min(Math.Max(point.Y, screen.Top + margin), screen.Bottom - size.Height - margin);
        return new Point(x, y);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
        if (!_popup.IsDisposed)
        {
            _popup.Dispose();
        }
        base.ExitThreadCore();
    }
}
