using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaScope.Providers.Codex;

internal sealed class CodexUsageProvider : IUsageProvider
{
    private readonly CodexAppServerClient _client;

    public string Id => RateLimitMapper.ProviderId;
    public string DisplayName => RateLimitMapper.ProviderDisplayName;
    public string ResolvedCommandText => _client.ResolvedCommandText;

    public event Action<ProviderUsage>? UsageUpdated;

    public CodexUsageProvider(ProviderSettings settings)
    {
        _client = new CodexAppServerClient(settings);
        _client.RateLimitsUpdated += usage => UsageUpdated?.Invoke(usage);
    }

    public Task<ProviderUsage> ReadAsync(CancellationToken ct) => _client.ReadRateLimitsAsync(ct);

    public Task<ProviderUsage> ReconnectAsync(CancellationToken ct) => _client.RestartAsync(ct);

    public void Dispose() => _client.Dispose();
}
