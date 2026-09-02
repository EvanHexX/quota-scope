using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace QuotaScope.Providers.Claude;

// Claude Code owns OAuth refresh; QuotaScope only nudges it. The access token
// lives 8 hours, so an unattended tray app goes dark once Claude Code has not
// run for that long. Shortly before expiry (and again after a 401) this runs
// `claude mcp list` headless - a read-only command that costs no quota - and
// lets Claude Code rewrite the credentials file itself. No token material is
// read, stored, or logged here, and the command output is discarded because it
// describes the user's configured servers.
internal sealed class ClaudeSessionRenewer : IDisposable
{
    // Which command renews is a property of the CLI, not a documented contract:
    // it has to be one that needs a live token. Measured 2026-08-13 against
    // claude 2.x with a token five hours past expiry - `auth status` reports
    // loggedIn straight from the stored refresh token and never rewrites the
    // file, while `mcp list` refreshes it (~2.2s). If a future CLI stops
    // renewing here, the failure path below degrades to the manual sign-in.
    private const string RenewalCommand = "mcp list";

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

    // A nudge sent while the token is still valid may legitimately do nothing:
    // Claude Code decides for itself whether a live token is close enough to
    // expiry to be worth refreshing, and its threshold is not ours to know.
    // Retry those on a flat interval instead of the escalating one.
    public static readonly TimeSpan ProactiveRetry = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private DateTime _attemptStampUtc;
    private int _consecutiveFailures;
    private bool _running;
    private bool _attemptIsRecovery;
    private Process? _current;
    private bool _disposed;

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
            if (_disposed) return;
            _consecutiveFailures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
        }
    }

    // Fire-and-forget: the credentials-file watcher (or the next poll) picks up
    // the result, so polling never blocks on the child process. Returns whether
    // an attempt was actually started.
    //
    // `recovery` means the token is already dead (past expiry, or the endpoint
    // answered 401), so a nudge that changes nothing really is a failure. Only
    // those drive the escalating backoff and the give-up counter - counting
    // proactive no-ops would exhaust the budget before the token even expired
    // and leave the app stranded through the outage it exists to prevent.
    public bool RequestRenewal(bool recovery)
    {
        lock (_sync)
        {
            if (_disposed || _running || DateTimeOffset.Now < _nextAttemptAt) return false;
            _running = true;
            _attemptIsRecovery = recovery;
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
            renewed = RunRenewalCommand()
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

    // Delay before the next attempt, given how the last one ended. `failures` is
    // the running count *after* this attempt. Null means "stop until the
    // credentials file changes".
    public static TimeSpan? NextDelayAfter(bool renewed, bool recovery, int failures)
    {
        if (renewed) return TimeSpan.Zero;
        // Token was still valid, so a no-op proves nothing about the trigger.
        if (!recovery) return ProactiveRetry;
        return CooldownAfter(failures);
    }

    private void CompleteAttempt(bool renewed)
    {
        lock (_sync)
        {
            _running = false;
            var recovery = _attemptIsRecovery;
            if (renewed) _consecutiveFailures = 0;
            else if (recovery) _consecutiveFailures++;

            var delay = NextDelayAfter(renewed, recovery, _consecutiveFailures);
            _nextAttemptAt = delay is null ? DateTimeOffset.MaxValue : DateTimeOffset.Now + delay.Value;
        }
    }

    private bool RunRenewalCommand()
    {
        var spec = ClaudeCommandResolver.Resolve(RenewalCommand);
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

        lock (_sync)
        {
            // Shutdown raced the launch; do not leave the child behind.
            if (_disposed)
            {
                KillQuietly(process);
                return false;
            }
            _current = process;
        }

        try
        {
            // Drain both pipes without keeping the payload: the output describes
            // the user's configured servers and must not be logged anywhere.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();

            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                KillQuietly(process);
                return false;
            }
            return process.ExitCode == 0;
        }
        finally
        {
            lock (_sync) { _current = null; }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or it exited between the check and the kill.
        }
    }

    // The tray app can exit mid-nudge, and once it does nothing else would ever
    // reap the CLI it launched: the worker thread waiting on the child dies with
    // the process, so the timeout kill above never runs. A stranded `claude`
    // keeps its executable locked, which is enough to make a later CLI update
    // fail until the machine is restarted.
    public void Dispose()
    {
        Process? current;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _nextAttemptAt = DateTimeOffset.MaxValue;
            current = _current;
        }
        if (current is not null) KillQuietly(current);
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

        // A proactive no-op must neither escalate nor ever give up: the token is
        // still alive, so the trigger has not been shown to be broken.
        var proactiveChecks =
            NextDelayAfter(renewed: false, recovery: false, failures: 0) == ProactiveRetry
            && NextDelayAfter(renewed: false, recovery: false, failures: MaxConsecutiveFailures) == ProactiveRetry
            && NextDelayAfter(renewed: true, recovery: false, failures: 0) == TimeSpan.Zero;

        // A recovery no-op is a real failure and escalates to the give-up point.
        var recoveryChecks =
            NextDelayAfter(renewed: false, recovery: true, failures: 1) == Cooldowns[0]
            && NextDelayAfter(renewed: false, recovery: true, failures: 3) == Cooldowns[2]
            && NextDelayAfter(renewed: false, recovery: true, failures: MaxConsecutiveFailures) is null
            && NextDelayAfter(renewed: true, recovery: true, failures: 4) == TimeSpan.Zero;

        return expiryChecks && cooldownChecks && proactiveChecks && recoveryChecks;
    }
}
