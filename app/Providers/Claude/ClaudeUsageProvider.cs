using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaScope.Providers.Claude;

// Polls the undocumented Anthropic OAuth usage endpoint (same data as the
// /usage slash command in Claude Code). May break without notice.
internal sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";
    // Without a claude-code User-Agent the endpoint persistently returns 429.
    private const string UserAgent = "claude-code/2.0.0";
    private static readonly TimeSpan[] BackoffDelays =
    {
        TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)
    };
    // Without auto-renew the session dies unattended, so warn well ahead.
    private static readonly TimeSpan ManualSessionWarning = TimeSpan.FromMinutes(60);
    // Credential writes arrive as a burst of events; coalesce them.
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ClaudeSessionRenewer _renewer = new();
    private readonly TimeSpan _minPollInterval;
    private readonly bool _autoRenew;
    private FileSystemWatcher? _watcher;
    private int _watcherBusy;
    private ProviderUsage? _lastSuccess;
    private ProviderUsage? _lastResult;
    private DateTimeOffset _nextFetchAt = DateTimeOffset.MinValue;
    private int _backoffStep;
    private bool _authPaused;
    private DateTime _authPausedFileStampUtc;
    private bool _disposed;

    public string Id => ClaudeUsageMapper.ProviderId;
    public string DisplayName => ClaudeUsageMapper.ProviderDisplayName;

    // Raised when the credentials file changes, so a sign-in or a token refresh
    // shows up immediately instead of at the next poll.
    public event Action<ProviderUsage>? UsageUpdated;

    public ClaudeUsageProvider(ProviderSettings settings)
    {
        // Poll interval floor is 60 seconds regardless of configuration.
        _minPollInterval = TimeSpan.FromSeconds(Math.Max(60, settings.RefreshSeconds));
        _autoRenew = settings.AutoRenewSession;
        TryStartCredentialsWatcher();
    }

    public Task<ProviderUsage> ReadAsync(CancellationToken ct) => FetchAsync(force: false, ct);

    public Task<ProviderUsage> ReconnectAsync(CancellationToken ct)
    {
        _authPaused = false;
        _backoffStep = 0;
        _nextFetchAt = DateTimeOffset.MinValue;
        // An explicit reconnect also clears a renewer that had given up.
        _renewer.Reset();
        return FetchAsync(force: true, ct);
    }

    // The watcher and the poll timer both call in; serialise so the cache and
    // backoff state stay consistent.
    private async Task<ProviderUsage> FetchAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchCoreAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProviderUsage> FetchCoreAsync(bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var expiresAt = ClaudeCredentialReader.TryReadExpiresAt();

        // Proactive renewal: hand the refresh back to Claude Code before the
        // token dies, so polling never has to fall into the 401 pause. Past the
        // expiry stamp the nudge is no longer speculative.
        if (_autoRenew && ClaudeSessionRenewer.IsExpiring(expiresAt, now))
        {
            _renewer.RequestRenewal(recovery: expiresAt is null || expiresAt <= now);
        }

        if (_authPaused && !force)
        {
            // Resume only after Claude Code rewrites the credentials file.
            if (ClaudeCredentialReader.GetCredentialsFileStampUtc() == _authPausedFileStampUtc)
            {
                if (_autoRenew)
                {
                    _renewer.RequestRenewal(recovery: true);
                }
                // Re-emit rather than replay the cached message, so a renewal
                // that started (or gave up) is reflected while still paused.
                return PauseForAuth();
            }
            // Something rewrote the credentials: clear any renewal backoff even
            // when the file watcher is unavailable.
            _authPaused = false;
            _renewer.Reset();
        }

        if (!force && now < _nextFetchAt && _lastResult is not null)
        {
            return _lastResult;
        }

        var token = ClaudeCredentialReader.TryReadAccessToken();
        if (string.IsNullOrEmpty(token))
        {
            return PauseForAuth();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token died before the proactive window could fire (app
                // asleep, clock jump); ask Claude Code to renew right away.
                if (_autoRenew)
                {
                    _renewer.RequestRenewal(recovery: true);
                }
                return PauseForAuth();
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = BackoffDelays[Math.Min(_backoffStep, BackoffDelays.Length - 1)];
                _backoffStep = Math.Min(_backoffStep + 1, BackoffDelays.Length - 1);
                _nextFetchAt = now + delay;
                return _lastResult = WithFallback(ProviderState.RateLimited, $"Rate limited. Retrying in {FormatDelay(delay)}");
            }
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            DumpLastResponse(body);
            var usage = ClaudeUsageMapper.FromJson(doc.RootElement) with { StatusText = SessionStatus(expiresAt, now) };
            _backoffStep = 0;
            _authPaused = false;
            _nextFetchAt = now + _minPollInterval;
            _lastSuccess = usage;
            return _lastResult = usage;
        }
        catch (Exception)
        {
            // Network/timeout/parse failure: keep showing the last good data as stale.
            _nextFetchAt = now + _minPollInterval;
            return _lastResult = WithFallback(ProviderState.Stale, StaleStatus());
        }
    }

    // Instant recovery: when Claude Code rewrites the credentials file (sign-in
    // or token refresh) push a fresh read instead of waiting for the next poll.
    private void TryStartCredentialsWatcher()
    {
        try
        {
            var directory = ClaudeCredentialReader.CredentialsDirectory;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

            _watcher = new FileSystemWatcher(directory, ClaudeCredentialReader.CredentialsFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnCredentialsChanged;
            _watcher.Created += OnCredentialsChanged;
            _watcher.Renamed += OnCredentialsChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Watching is an optimisation only; polling still recovers on its own.
            _watcher = null;
        }
    }

    private void OnCredentialsChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // One in-flight reaction at a time; a rewrite emits several events.
        if (Interlocked.Exchange(ref _watcherBusy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WatcherDebounce).ConfigureAwait(false);
                if (_disposed) return;
                _renewer.Reset();
                var usage = await FetchAsync(force: true, CancellationToken.None).ConfigureAwait(false);
                UsageUpdated?.Invoke(usage);
            }
            catch
            {
                // A failed push leaves the poll loop untouched.
            }
            finally
            {
                Interlocked.Exchange(ref _watcherBusy, 0);
            }
        });
    }

    private ProviderUsage PauseForAuth()
    {
        _authPaused = true;
        _authPausedFileStampUtc = ClaudeCredentialReader.GetCredentialsFileStampUtc();
        return _lastResult = ProviderUsage.Offline(Id, DisplayName, AuthPausedStatus(), ProviderState.Unauthenticated);
    }

    // A renewal in flight is the actionable state; otherwise fall back to the
    // sign-in hint, worded by whether credentials exist at all.
    private string AuthPausedStatus()
    {
        if (_renewer.IsRenewing) return "Renewing Claude session";
        return ClaudeCredentialReader.CredentialsFileExists()
            ? "Claude session expired. Run Claude Code and sign in"
            : "Run Claude Code and sign in";
    }

    // Surfaces the OAuth session next to the usage rows: renewal in flight, or
    // a countdown once expiry is close enough to matter.
    private string SessionStatus(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (_renewer.IsRenewing) return "Renewing Claude session";
        if (expiresAt is { } value)
        {
            var remaining = value - now;
            if (remaining <= TimeSpan.Zero) return "Claude session expired";
            var warnWithin = _autoRenew ? ClaudeSessionRenewer.RenewLeadTime : ManualSessionWarning;
            if (remaining <= warnWithin) return $"Session expires in {FormatDelay(remaining)}";
        }
        return "Claude rate limit";
    }

    private ProviderUsage WithFallback(ProviderState state, string status)
    {
        if (_lastSuccess is null)
        {
            return ProviderUsage.Offline(Id, DisplayName, status, state == ProviderState.Stale ? ProviderState.Unavailable : state);
        }
        return _lastSuccess with { State = state, StatusText = status };
    }

    private string StaleStatus()
    {
        return _lastSuccess is null
            ? "Claude usage unavailable"
            : $"Stale - last {_lastSuccess.UpdatedAt.ToLocalTime():h:mm tt}";
    }

    // Local-only diagnostic: the last successful response body (usage numbers,
    // no credentials) next to the exe, for verifying live schema changes.
    private static void DumpLastResponse(string body)
    {
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "claude_usage_last.json"), body);
        }
        catch
        {
        }
    }

    private static string FormatDelay(TimeSpan delay)
    {
        return delay.TotalMinutes >= 1 ? $"{(int)delay.TotalMinutes}m" : $"{(int)delay.TotalSeconds}s";
    }

    public void Dispose()
    {
        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnCredentialsChanged;
            _watcher.Created -= OnCredentialsChanged;
            _watcher.Renamed -= OnCredentialsChanged;
            _watcher.Dispose();
            _watcher = null;
        }
        _http.Dispose();
        _gate.Dispose();
    }
}
