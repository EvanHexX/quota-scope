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

internal sealed record RateLimitWindow(double UsedPercent, DateTimeOffset? ResetsAt, long? WindowDurationMins)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

// Window is null for text-only rows (e.g. credits balance); DetailText carries the value.
internal sealed record UsageRow(string Label, RateLimitWindow? Window, bool IsPrimary, string? DetailText = null);

internal sealed record ProviderUsage(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<UsageRow> Rows,
    double OverallRemainingPercent,
    string StatusText,
    DateTimeOffset UpdatedAt,
    ProviderState State)
{
    public static ProviderUsage Offline(string providerId, string displayName, string message, ProviderState state) =>
        new(providerId, displayName, Array.Empty<UsageRow>(), 0d, message, DateTimeOffset.Now, state);
}
