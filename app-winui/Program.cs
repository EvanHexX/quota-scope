using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using QuotaScope.Hotkeys;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;
using QuotaScope.WinUI.Windows;

namespace QuotaScope.WinUI;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\QuotaScope.WinUI.SingleInstance";
    private const string TogglePopupEventName = @"Local\QuotaScope.WinUI.TogglePopup";

    // Set when this instance replaced one whose graphics adapters changed
    // (GraphicsAdapters): it ignores the simulation variable, silently records
    // alert points already passed, and reopens the popup if that one had it open.
    public static bool RestartedForGraphics { get; private set; }
    public static bool ReopenPopup { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // `--restart-after <pid> graphics [popup]`: let the instance being
        // replaced exit first, or the single-instance check below would hand
        // straight back to it and this one would end.
        if (args.Length > 0 && args[0].Equals("--restart-after", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1 && int.TryParse(args[1], out var previousPid))
            {
                try
                {
                    using var previous = System.Diagnostics.Process.GetProcessById(previousPid);
                    previous.WaitForExit(15000);
                }
                catch
                {
                    // Already gone.
                }
            }
            RestartedForGraphics = Array.Exists(args, a => a.Equals("graphics", StringComparison.OrdinalIgnoreCase));
            ReopenPopup = Array.Exists(args, a => a.Equals("popup", StringComparison.OrdinalIgnoreCase));
            args = Array.Empty<string>();
        }

        // Must run before any XAML/WinRT initialization so it stays headless.
        if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitMapper.RunSelfTest()
                && ClaudeUsageMapper.RunSelfTest()
                && ClaudeSessionRenewer.RunSelfTest()
                && HotkeyDefinition.RunSelfTest()
                && AppSettings.RunSelfTest()
                && RowShapes.RunSelfTest()
                && RowLayout.RunSelfTest()
                && UsageAlerts.RunSelfTest()
                && PopupPalette.RunSelfTest()
                && GraphicsAdapters.RunSelfTest()
                && Loc.RunSelfTest() ? 0 : 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        using var toggleSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TogglePopupEventName);
        if (!isFirstInstance)
        {
            // Second launch just asks the running instance to toggle its popup.
            toggleSignal.Set();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(toggleSignal);
        });
        return 0;
    }
}
