using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaScope.Providers;

internal interface IUsageProvider : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    Task<ProviderUsage> ReadAsync(CancellationToken ct);
    Task<ProviderUsage> ReconnectAsync(CancellationToken ct);
    // Only providers with push support raise this.
    event Action<ProviderUsage>? UsageUpdated;
}
