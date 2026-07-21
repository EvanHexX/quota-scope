using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuotaScope;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _notifyIcon;
    private UsagePopupForm _popup;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CodexAppServerClient _client;
    private readonly HotkeyWindow _hotkeyWindow;
    private UsageViewModel _current = UsageViewModel.Offline("Waiting for Codex connection");
    private bool _refreshing;

    public TrayApplicationContext()
    {
        _popup = CreatePopup();
        _client = new CodexAppServerClient(_settings);
        _client.RateLimitsUpdated += UpdateUsage;

        _notifyIcon = new NotifyIcon
        {
            Text = "Checking Codex usage",
            Icon = TrayIconRenderer.CreateUsageIcon(100, false),
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

        _timer.Interval = Math.Max(10, _settings.RefreshSeconds) * 1000;
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
        settings.DropDownItems.Add(BuildCodexConnectionMenu());
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

    private ToolStripMenuItem BuildCodexConnectionMenu()
    {
        var item = new ToolStripMenuItem("Codex Connection");
        item.DropDownItems.Add("Reconnect", null, async (_, _) => await ReconnectAsync().ConfigureAwait(false));
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem($"Command: {_client.ResolvedCommandText}") { Enabled = false });
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
        var spark = new ToolStripMenuItem("GPT-5.3 Spark")
        {
            Checked = _settings.ShowSparkUsage,
            CheckOnClick = false
        };
        spark.Click += (_, _) =>
        {
            _settings.ShowSparkUsage = !_settings.ShowSparkUsage;
            _settings.Save();
            var popup = GetPopup();
            popup.ApplySettings(_settings);
            RefreshMenu();
            if (popup.Visible)
            {
                PositionPopup(popup);
            }
        };
        item.DropDownItems.Add(spark);
        return item;
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
        }
        _popup.ContextMenuStrip = _notifyIcon.ContextMenuStrip;
        _popup.ApplySettings(_settings);
        return _popup;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var usage = await _client.ReadRateLimitsAsync(cts.Token).ConfigureAwait(false);
            UpdateUsage(usage);
        }
        catch (Exception ex)
        {
            UpdateUsage(UsageViewModel.Offline(FormatConnectionError(ex)));
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
            UpdateUsage(UsageViewModel.Offline("Connecting to Codex..."));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var usage = await _client.RestartAsync(cts.Token).ConfigureAwait(false);
            UpdateUsage(usage);
        }
        catch (Exception ex)
        {
            UpdateUsage(UsageViewModel.Offline(FormatConnectionError(ex)));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string FormatConnectionError(Exception ex)
    {
        return ex is OperationCanceledException
            ? "Codex connection timed out. Use Settings > Codex Connection > Reconnect."
            : "Codex connection required: " + ex.Message;
    }

    private void UpdateUsage(UsageViewModel usage)
    {
        if (Application.MessageLoop && !_popup.IsDisposed && _popup.InvokeRequired)
        {
            _popup.BeginInvoke(new Action(() => UpdateUsage(usage)));
            return;
        }

        var popup = GetPopup();
        _current = usage;
        var low = usage.OverallRemainingPercent <= _settings.WarningThresholdPercent;
        _notifyIcon.Text = _settings.ShowSparkUsage
            ? $"Codex 5h {FormatPercent(usage.FiveHour)} / 1w {FormatPercent(usage.OneWeek)} / Spark 5h {FormatPercent(usage.SparkFiveHour)} / 1w {FormatPercent(usage.SparkOneWeek)}"
            : $"Codex 5h {FormatPercent(usage.FiveHour)} / 1w {FormatPercent(usage.OneWeek)}";
        var oldIcon = _notifyIcon.Icon;
        _notifyIcon.Icon = TrayIconRenderer.CreateUsageIcon(usage.OverallRemainingPercent, low);
        oldIcon?.Dispose();
        popup.SetUsage(usage);
    }

    private static string FormatPercent(RateLimitWindow? window)
    {
        return window is null ? "--%" : $"{window.RemainingPercent}%";
    }

    private void TogglePopup()
    {
        var popup = GetPopup();
        if (popup.Visible)
        {
            popup.Hide();
            return;
        }

        popup.SetUsage(_current);
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
            _popup.SetUsage(_current);
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
        _client.Dispose();
        if (!_popup.IsDisposed)
        {
            _popup.Dispose();
        }
        base.ExitThreadCore();
    }
}





