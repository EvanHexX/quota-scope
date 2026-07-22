using System;
using System.Collections.Generic;

namespace QuotaScope.Providers;

internal enum ProviderState
{
    Ok,
    Unauthenticated,
    Unavailable,
    RateLimited,
    Stale
}

// All layers speak usedPercent (0 = untouched, 100 = exhausted); there is no
// remaining-percent anywhere in the model.
internal sealed record RateLimitWindow(double UsedPercent, DateTimeOffset? ResetsAt, long? WindowDurationMins);

// Window is null for text-only rows (e.g. credits balance); DetailText carries the value.
internal sealed record UsageRow(string Label, RateLimitWindow? Window, bool IsPrimary, string? DetailText = null);

internal sealed record ProviderUsage(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<UsageRow> Rows,
    double OverallUsedPercent,
    string StatusText,
    DateTimeOffset UpdatedAt,
    ProviderState State)
{
    public static ProviderUsage Offline(string providerId, string displayName, string message, ProviderState state) =>
        new(providerId, displayName, Array.Empty<UsageRow>(), 0d, message, DateTimeOffset.Now, state);
}
