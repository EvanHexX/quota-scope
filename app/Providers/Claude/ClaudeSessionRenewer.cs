using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace QuotaScope.Providers.Claude;

// Claude Code owns OAuth refresh; QuotaScope only nudges it. The access token
// lives 8 hours, so an unattended tray app goes dark once Claude Code has not
// run for that long. Shortly before expiry (and again after a 401) this runs
// `claude auth status` headless - a read-only command that costs no quota - and
// lets Claude Code rewrite the credentials file itself. No token material is
// read, stored, or logged here, and the command output is discarded because it
// carries account identity.
internal sealed class ClaudeSessionRenewer
{
    // Start nudging this long before expiry so a renewal lands before the
    // first 401 rather than after it.
    public static readonly TimeSpan RenewLeadTime = TimeSpan.FromMinutes(15);

    // Escalating cooldowns for attempts that did not rewrite the credentials
    // file (Claude Code missing from PATH, refresh token revoked, ...).
    private static readonly TimeSpan[] Cooldowns =
    {
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)
    };

    // After this many failures in a row, stop spawning processes entirely and
    // wait for the credentials file to change (a manual sign-in calls Reset).
    public const int MaxConsecutiveFailures = 5;

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private DateTime _attemptStampUtc;
    private int _consecutiveFailures;
    private bool _running;

    public bool IsRenewing
    {
        get { lock (_sync) return _running; }
    }

    // True only when expiry is known and close; an unreadable expiry must not
    // turn into a nudge on every poll.
    public static bool IsExpiring(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        return expiresAt is { } value && value - now <= RenewLeadTime;
    }

    // Cooldown after `consecutiveFailures` failed attempts; null once the
    // renewer has given up until the credentials file changes.
    public static TimeSpan? CooldownAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;
        if (consecutiveFailures >= MaxConsecutiveFailures) return null;
        return Cooldowns[Math.Min(consecutiveFailures - 1, Cooldowns.Length - 1)];
    }

    // Called when the credentials file changes: whatever rewrote it (Claude
    // Code, a manual sign-in) cleared the condition we were backing off from.
    public void Reset()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
        }
    }

    // Fire-and-forget: the credentials-file watcher (or the next poll) picks up
    // the result, so polling never blocks on the child process. Returns whether
    // an attempt was actually started.
    public bool RequestRenewal()
    {
        lock (_sync)
        {
            if (_running || DateTimeOffset.Now < _nextAttemptAt) return false;
            _running = true;
            _attemptStampUtc = ClaudeCredentialReader.GetCredentialsFileStampUtc();
        }

        if (!ThreadPool.QueueUserWorkItem(_ => RunAttempt()))
        {
            CompleteAttempt(renewed: false);
            return false;
        }
        return true;
    }

    private void RunAttempt()
    {
        var renewed = false;
        try
        {
            renewed = RunAuthStatus()
                && ClaudeCredentialReader.GetCredentialsFileStampUtc() != _attemptStampUtc;
        }
        catch
        {
            // Renewal is best-effort; the provider still shows the sign-in hint.
        }
        finally
        {
            CompleteAttempt(renewed);
        }
    }

    private void CompleteAttempt(bool renewed)
    {
        lock (_sync)
        {
            _running = false;
            _consecutiveFailures = renewed ? 0 : _consecutiveFailures + 1;
            var cooldown = CooldownAfter(_consecutiveFailures);
            _nextAttemptAt = cooldown is null ? DateTimeOffset.MaxValue : DateTimeOffset.Now + cooldown.Value;
        }
    }

    private static bool RunAuthStatus()
    {
        var spec = ClaudeCommandResolver.Resolve("auth status");
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Run outside the app directory so no project-trust prompt can
            // block the child process.
            WorkingDirectory = SafeWorkingDirectory()
        };

        using var process = Process.Start(startInfo);
        if (process is null) return false;

        // Drain both pipes without keeping the payload: `auth status` prints the
        // signed-in account, which must not be logged anywhere.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
            return false;
        }
        return process.ExitCode == 0;
    }

    private static string SafeWorkingDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(userProfile) ? userProfile : AppContext.BaseDirectory;
    }

    public static bool RunSelfTest()
    {
        var now = DateTimeOffset.Now;
        var expiryChecks =
            !IsExpiring(null, now)
            && !IsExpiring(now + TimeSpan.FromHours(8), now)
            && !IsExpiring(now + RenewLeadTime + TimeSpan.FromMinutes(1), now)
            && IsExpiring(now + TimeSpan.FromMinutes(5), now)
            && IsExpiring(now - TimeSpan.FromHours(1), now);

        var cooldownChecks =
            CooldownAfter(0) == TimeSpan.Zero
            && CooldownAfter(1) == Cooldowns[0]
            && CooldownAfter(2) == Cooldowns[1]
            && CooldownAfter(3) == Cooldowns[2]
            && CooldownAfter(4) == Cooldowns[^1]
            && CooldownAfter(MaxConsecutiveFailures) is null;

        return expiryChecks && cooldownChecks;
    }
}
