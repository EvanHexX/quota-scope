using System;
using System.Text.Json;

namespace QuotaScope;

internal static class RateLimitMapper
{
    public static UsageViewModel FromJsonResult(JsonElement result)
    {
        var mainSnapshotElement = result.TryGetProperty("rateLimits", out var direct)
            ? direct
            : result;
        var mainSnapshot = ParseSnapshot(mainSnapshotElement);
        var sparkSnapshot = TryFindSparkSnapshot(result);
        return ToViewModel(mainSnapshot, sparkSnapshot);
    }

    public static UsageViewModel ToViewModel(RateLimitSnapshot snapshot, RateLimitSnapshot? sparkSnapshot = null)
    {
        var fiveHour = PickWindow(snapshot, maxDurationMins: 24 * 60, preferShortest: true);
        var oneWeek = PickWindow(snapshot, minDurationMins: 6 * 24 * 60, preferShortest: false);
        var sparkFiveHour = sparkSnapshot is null ? null : PickWindow(sparkSnapshot, maxDurationMins: 24 * 60, preferShortest: true);
        var sparkOneWeek = sparkSnapshot is null ? null : PickWindow(sparkSnapshot, minDurationMins: 6 * 24 * 60, preferShortest: false);
        return new UsageViewModel(
            snapshot.OverallRemainingPercent,
            fiveHour,
            oneWeek,
            sparkFiveHour,
            sparkOneWeek,
            "Spark",
            string.IsNullOrWhiteSpace(snapshot.RateLimitReachedType) ? "Codex rate limit" : snapshot.RateLimitReachedType!,
            DateTimeOffset.Now);
    }

    public static RateLimitSnapshot ParseSnapshot(JsonElement element)
    {
        return new RateLimitSnapshot(
            GetString(element, "limitId"),
            GetString(element, "limitName"),
            GetString(element, "planType"),
            ParseWindow(GetNullableProperty(element, "primary")),
            ParseWindow(GetNullableProperty(element, "secondary")),
            GetString(element, "rateLimitReachedType"));
    }

    private static RateLimitSnapshot? TryFindSparkSnapshot(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) || byLimitId.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in byLimitId.EnumerateObject())
        {
            var snapshot = ParseSnapshot(property.Value);
            var searchable = $"{property.Name} {snapshot.LimitId} {snapshot.LimitName}";
            if (searchable.Contains("spark", StringComparison.OrdinalIgnoreCase)
                || searchable.Contains("bengalfox", StringComparison.OrdinalIgnoreCase)
                || searchable.Contains("gpt-5.3-codex", StringComparison.OrdinalIgnoreCase))
            {
                return snapshot;
            }
        }

        return null;
    }

    private static RateLimitWindow? PickWindow(RateLimitSnapshot snapshot, long? minDurationMins = null, long? maxDurationMins = null, bool preferShortest = true)
    {
        RateLimitWindow? best = null;
        foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })
        {
            if (window is null) continue;
            var duration = window.WindowDurationMins;
            if (minDurationMins.HasValue && (!duration.HasValue || duration.Value < minDurationMins.Value)) continue;
            if (maxDurationMins.HasValue && (!duration.HasValue || duration.Value > maxDurationMins.Value)) continue;
            if (best is null)
            {
                best = window;
                continue;
            }

            var currentDuration = duration ?? 0;
            var bestDuration = best.WindowDurationMins ?? 0;
            if (preferShortest ? currentDuration < bestDuration : currentDuration > bestDuration)
            {
                best = window;
            }
        }

        return best;
    }

    private static RateLimitWindow? ParseWindow(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var usedPercent = GetInt(element.Value, "usedPercent") ?? 0;
        var resetsAt = ParseTimestamp(GetLong(element.Value, "resetsAt"));
        var duration = GetLong(element.Value, "windowDurationMins");
        return new RateLimitWindow(Math.Clamp(usedPercent, 0, 100), resetsAt, duration);
    }

    private static JsonElement? GetNullableProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            return value;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(long? raw)
    {
        if (!raw.HasValue) return null;
        return raw.Value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw.Value)
            : DateTimeOffset.FromUnixTimeSeconds(raw.Value);
    }

    public static bool RunSelfTest()
    {
        const string sample = @"
        {
          ""rateLimits"": {
            ""limitId"": ""codex"",
            ""limitName"": ""Codex"",
            ""planType"": ""plus"",
            ""primary"": { ""usedPercent"": 37, ""resetsAt"": 1893499200, ""windowDurationMins"": 300 },
            ""secondary"": { ""usedPercent"": 12, ""resetsAt"": 1894053600, ""windowDurationMins"": 10080 }
          },
          ""rateLimitsByLimitId"": {
            ""codex_bengalfox"": {
              ""limitId"": ""codex_bengalfox"",
              ""limitName"": ""GPT-5.3-Codex-Spark"",
              ""primary"": { ""usedPercent"": 4, ""resetsAt"": 1893495600, ""windowDurationMins"": 300 },
              ""secondary"": { ""usedPercent"": 8, ""resetsAt"": 1894053600, ""windowDurationMins"": 10080 }
            }
          }
        }";

        using var doc = JsonDocument.Parse(sample);
        var vm = FromJsonResult(doc.RootElement);
        return vm.OverallRemainingPercent == 63
            && vm.FiveHour?.RemainingPercent == 63
            && vm.OneWeek?.RemainingPercent == 88
            && vm.SparkFiveHour?.RemainingPercent == 96
            && vm.SparkOneWeek?.RemainingPercent == 92
            && vm.SparkLabel == "Spark";
    }
}
