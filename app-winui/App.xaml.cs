using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace QuotaScope.WinUI;

public partial class App : Application
{
    private readonly EventWaitHandle _toggleSignal;
    private TrayController? _tray;

    internal App(EventWaitHandle toggleSignal)
    {
        _toggleSignal = toggleSignal;
        InitializeComponent();

        // A tray utility should survive and log instead of dying silently.
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("xaml-unhandled", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("appdomain-unhandled", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("task-unobserved", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Windowless lifecycle: the tray controller owns everything; no main window.
        _tray = new TrayController();
        StartToggleSignalListener(DispatcherQueue.GetForCurrentThread());
    }

    private void StartToggleSignalListener(DispatcherQueue dispatcherQueue)
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                _toggleSignal.WaitOne();
                dispatcherQueue.TryEnqueue(() => _tray?.TogglePopup());
            }
        })
        {
            IsBackground = true,
            Name = "QuotaScope.ToggleSignal"
        };
        listener.Start();
    }
}
