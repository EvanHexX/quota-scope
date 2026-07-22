using System;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace QuotaScope.WinUI.Tray;

// Thin seam over the tray implementation so H.NotifyIcon can be swapped for a
// raw Shell_NotifyIcon fallback without touching TrayController.
internal interface ITrayIcon : IDisposable
{
    event Action? LeftClicked;
    void SetIcon(System.Drawing.Icon icon);
    void SetTooltip(string text);
    void SetMenu(MenuFlyout menu);
    void ShowNotification(string title, string message);
    void Show();
}

internal sealed class TrayIconHost : ITrayIcon
{
    private readonly TaskbarIcon _taskbarIcon = new();
    private System.Drawing.Icon? _currentIcon;

    public event Action? LeftClicked;

    public TrayIconHost()
    {
        _taskbarIcon.ContextMenuMode = ContextMenuMode.SecondWindow;
        _taskbarIcon.LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke());
    }

    public void SetIcon(System.Drawing.Icon icon)
    {
        var previous = _currentIcon;
        _taskbarIcon.Icon = icon;
        _currentIcon = icon;
        previous?.Dispose();
    }

    public void SetTooltip(string text) => _taskbarIcon.ToolTipText = text;

    public void SetMenu(MenuFlyout menu) => _taskbarIcon.ContextFlyout = menu;

    public void ShowNotification(string title, string message)
    {
        try
        {
            _taskbarIcon.ShowNotification(title, message);
        }
        catch
        {
            // Notifications are best-effort; never take the app down for one.
        }
    }

    public void Show() => _taskbarIcon.ForceCreate();

    public void Dispose()
    {
        _taskbarIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
