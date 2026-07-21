using System;

namespace QuotaScope;

internal sealed record RateLimitWindow(int UsedPercent, DateTimeOffset? ResetsAt, long? WindowDurationMins)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record RateLimitSnapshot(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    string? RateLimitReachedType)
{
    public int OverallRemainingPercent
    {
        get
        {
            var values = new[] { Primary?.RemainingPercent, Secondary?.RemainingPercent };
            var min = 100;
            var found = false;
            foreach (var value in values)
            {
                if (value.HasValue)
                {
                    min = Math.Min(min, value.Value);
                    found = true;
                }
            }
            return found ? min : 100;
        }
    }
}

internal sealed record UsageViewModel(
    int OverallRemainingPercent,
    RateLimitWindow? FiveHour,
    RateLimitWindow? OneWeek,
    RateLimitWindow? SparkFiveHour,
    RateLimitWindow? SparkOneWeek,
    string SparkLabel,
    string StatusText,
    DateTimeOffset UpdatedAt)
{
    public static UsageViewModel Offline(string message) => new(0, null, null, null, null, "Spark", message, DateTimeOffset.Now);
}
