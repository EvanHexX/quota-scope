using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;

namespace QuotaScope.WinUI;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\QuotaScope.WinUI.SingleInstance";
    private const string TogglePopupEventName = @"Local\QuotaScope.WinUI.TogglePopup";

    [STAThread]
    private static int Main(string[] args)
    {
        // Must run before any XAML/WinRT initialization so it stays headless.
        if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitMapper.RunSelfTest() && ClaudeUsageMapper.RunSelfTest() ? 0 : 1;
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
