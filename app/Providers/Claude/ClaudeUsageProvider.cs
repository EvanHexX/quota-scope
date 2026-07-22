using System;
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

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly TimeSpan _minPollInterval;
    private ProviderUsage? _lastSuccess;
    private ProviderUsage? _lastResult;
    private DateTimeOffset _nextFetchAt = DateTimeOffset.MinValue;
    private int _backoffStep;
    private bool _authPaused;
    private DateTime _authPausedFileStampUtc;

    public string Id => ClaudeUsageMapper.ProviderId;
    public string DisplayName => ClaudeUsageMapper.ProviderDisplayName;

    // Pull-only provider; the endpoint has no push channel so this never fires.
#pragma warning disable CS0067
    public event Action<ProviderUsage>? UsageUpdated;
#pragma warning restore CS0067

    public ClaudeUsageProvider(ProviderSettings settings)
    {
        // Poll interval floor is 60 seconds regardless of configuration.
        _minPollInterval = TimeSpan.FromSeconds(Math.Max(60, settings.RefreshSeconds));
    }

    public Task<ProviderUsage> ReadAsync(CancellationToken ct) => FetchAsync(force: false, ct);

    public Task<ProviderUsage> ReconnectAsync(CancellationToken ct)
    {
        _authPaused = false;
        _backoffStep = 0;
        _nextFetchAt = DateTimeOffset.MinValue;
        return FetchAsync(force: true, ct);
    }

    private async Task<ProviderUsage> FetchAsync(bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        if (_authPaused && !force)
        {
            // Resume only after Claude Code rewrites the credentials file.
            if (ClaudeCredentialReader.GetCredentialsFileStampUtc() == _authPausedFileStampUtc)
            {
                return _lastResult ?? PauseForAuth("Run Claude Code and sign in");
            }
            _authPaused = false;
        }

        if (!force && now < _nextFetchAt && _lastResult is not null)
        {
            return _lastResult;
        }

        var token = ClaudeCredentialReader.TryReadAccessToken();
        if (string.IsNullOrEmpty(token))
        {
            return PauseForAuth("Run Claude Code and sign in");
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
                return PauseForAuth("Claude session expired. Run Claude Code and sign in");
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
            var usage = ClaudeUsageMapper.FromJson(doc.RootElement);
            _backoffStep = 0;
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

    private ProviderUsage PauseForAuth(string message)
    {
        _authPaused = true;
        _authPausedFileStampUtc = ClaudeCredentialReader.GetCredentialsFileStampUtc();
        return _lastResult = ProviderUsage.Offline(Id, DisplayName, message, ProviderState.Unauthenticated);
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

    public void Dispose() => _http.Dispose();
}
