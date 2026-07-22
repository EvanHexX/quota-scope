using System;
using System.Diagnostics;

namespace QuotaScope.WinUI;

// Opens a terminal running the Claude Code login flow so the user only has to
// finish the browser sign-in. The provider resumes automatically once the
// credentials file appears/changes (ClaudeUsageProvider watches its timestamp).
internal static class ClaudeLoginLauncher
{
    public static bool TryLaunch()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /k keeps the terminal open so PATH errors stay visible.
                Arguments = "/k claude /login",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("claude-login-launch", ex);
            return false;
        }
    }
}
