using System;
using System.Windows.Forms;
using QuotaScope.Providers.Claude;
using QuotaScope.Providers.Codex;

namespace QuotaScope;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitMapper.RunSelfTest() && ClaudeUsageMapper.RunSelfTest() ? 0 : 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
