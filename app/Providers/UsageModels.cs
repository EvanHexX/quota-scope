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
// Label is the stable English key settings are stored under, so anything that
// only changes how a row is named goes in Scope instead: a display qualifier
// shown before the window name (Codex's plan on a weekly-only account).
// BeyondFull marks a credits row whose real amount lies past the configured
// full amount (a Codex balance above it, Claude spend beyond it): the gauge is
// clamped full, so the percent text has to say there is more. CreditsLeft is a
// credits row's amount left, which credits alerts compare against: the Codex
// balance (even with no gauge to draw), or what is left of the ceiling Claude
// spend is drawn against.
internal sealed record UsageRow(
    string Label,
    RateLimitWindow? Window,
    bool IsPrimary,
    string? DetailText = null,
    string? Scope = null,
    bool BeyondFull = false,
    double? CreditsLeft = null);

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
