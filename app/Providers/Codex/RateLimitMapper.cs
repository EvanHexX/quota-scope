using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace QuotaScope.Providers.Codex;

internal sealed record CodexCredits(bool HasCredits, bool Unlimited, string? Balance);

internal sealed record RateLimitSnapshot(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    string? RateLimitReachedType,
    CodexCredits? Credits);

internal static class RateLimitMapper
{
    public const string ProviderId = "codex";
    public const string ProviderDisplayName = "Codex";

    public static ProviderUsage FromJsonResult(JsonElement result)
    {
        var mainSnapshotElement = result.TryGetProperty("rateLimits", out var direct)
            ? direct
            : result;
        var mainSnapshot = ParseSnapshot(mainSnapshotElement);
        var sparkSnapshot = TryFindSparkSnapshot(result);
        return ToProviderUsage(mainSnapshot, sparkSnapshot);
    }

    public static ProviderUsage ToProviderUsage(RateLimitSnapshot snapshot, RateLimitSnapshot? sparkSnapshot = null)
    {
        var rows = new List<UsageRow>();

        // Rows are payload-driven: only windows that actually exist become rows.
        AddWindowRow(rows, snapshot.Primary, label => label, isPrimary: true);
        AddWindowRow(rows, snapshot.Secondary, label => label, isPrimary: true);
        if (sparkSnapshot is not null)
        {
            AddWindowRow(rows, sparkSnapshot.Primary, label => $"Spark {label}", isPrimary: false);
            AddWindowRow(rows, sparkSnapshot.Secondary, label => $"Spark {label}", isPrimary: false);
        }

        if (snapshot.Credits is { HasCredits: true } credits)
        {
            rows.Add(new UsageRow("Credits", null, IsPrimary: false, FormatCredits(credits)));
        }

        return new ProviderUsage(
            ProviderId,
            ProviderDisplayName,
            rows,
            ComputeOverallRemaining(snapshot),
            string.IsNullOrWhiteSpace(snapshot.RateLimitReachedType) ? "Codex rate limit" : snapshot.RateLimitReachedType!,
            DateTimeOffset.Now,
            ProviderState.Ok);
    }

    private static void AddWindowRow(List<UsageRow> rows, RateLimitWindow? window, Func<string, string> labelFactory, bool isPrimary)
    {
        if (window is null) return;
        rows.Add(new UsageRow(labelFactory(FormatDurationLabel(window.WindowDurationMins)), window, isPrimary));
    }

    public static string FormatDurationLabel(long? durationMins)
    {
        if (!durationMins.HasValue || durationMins.Value <= 0) return "usage";
        var mins = durationMins.Value;
        if (mins % 10080 == 0) return $"{mins / 10080}w";
        if (mins % 1440 == 0) return $"{mins / 1440}d";
        if (mins % 60 == 0) return $"{mins / 60}h";
        return $"{mins}m";
    }

    private static double ComputeOverallRemaining(RateLimitSnapshot snapshot)
    {
        var min = 100d;
        var found = false;
        foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })
        {
            if (window is null) continue;
            min = Math.Min(min, window.RemainingPercent);
            found = true;
        }
        return found ? min : 100d;
    }

    private static string FormatCredits(CodexCredits credits)
    {
        if (credits.Unlimited) return "unlimited";
        if (string.IsNullOrWhiteSpace(credits.Balance)) return "--";
        return decimal.TryParse(credits.Balance, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
            ? balance.ToString("0.##", CultureInfo.InvariantCulture)
            : credits.Balance!;
    }

    public static RateLimitSnapshot ParseSnapshot(JsonElement element)
    {
        return new RateLimitSnapshot(
            GetString(element, "limitId"),
            GetString(element, "limitName"),
            GetString(element, "planType"),
            ParseWindow(GetNullableProperty(element, "primary")),
            ParseWindow(GetNullableProperty(element, "secondary")),
            GetString(element, "rateLimitReachedType"),
            ParseCredits(GetNullableProperty(element, "credits")));
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

    private static RateLimitWindow? ParseWindow(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var usedPercent = GetDouble(element.Value, "usedPercent") ?? 0d;
        var resetsAt = ParseTimestamp(GetLong(element.Value, "resetsAt"));
        var duration = GetLong(element.Value, "windowDurationMins");
        return new RateLimitWindow(Math.Clamp(usedPercent, 0d, 100d), resetsAt, duration);
    }

    private static CodexCredits? ParseCredits(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var value = element.Value;
        var hasCredits = value.TryGetProperty("hasCredits", out var has) && has.ValueKind == JsonValueKind.True;
        var unlimited = value.TryGetProperty("unlimited", out var unl) && unl.ValueKind == JsonValueKind.True;
        return new CodexCredits(hasCredits, unlimited, GetString(value, "balance"));
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

    private static double? GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed))
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
        return RunLegacySchemaSelfTest() && RunWeeklyOnlySchemaSelfTest();
    }

    // Old schema: primary = 5h, secondary = 1w, integer percents.
    private static bool RunLegacySchemaSelfTest()
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
        var usage = FromJsonResult(doc.RootElement);
        return usage.Rows.Count == 4
            && NearlyEquals(usage.OverallRemainingPercent, 63)
            && RowMatches(usage.Rows[0], "5h", 63, isPrimary: true)
            && RowMatches(usage.Rows[1], "1w", 88, isPrimary: true)
            && RowMatches(usage.Rows[2], "Spark 5h", 96, isPrimary: false)
            && RowMatches(usage.Rows[3], "Spark 1w", 92, isPrimary: false);
    }

    // New schema (codex-cli 0.145.0-alpha.27): weekly-only primary, null secondary,
    // fractional percents, credits balance.
    private static bool RunWeeklyOnlySchemaSelfTest()
    {
        const string sample = @"
        {
          ""rateLimits"": {
            ""limitId"": ""codex"",
            ""limitName"": null,
            ""planType"": ""pro"",
            ""primary"": { ""usedPercent"": 12.5, ""resetsAt"": 1785269431, ""windowDurationMins"": 10080 },
            ""secondary"": null,
            ""credits"": { ""hasCredits"": true, ""unlimited"": false, ""balance"": ""146.0874125000"" },
            ""spendControlReached"": false,
            ""rateLimitReachedType"": null
          },
          ""rateLimitsByLimitId"": {
            ""codex"": {
              ""limitId"": ""codex"",
              ""primary"": { ""usedPercent"": 12.5, ""resetsAt"": 1785269431, ""windowDurationMins"": 10080 },
              ""secondary"": null
            },
            ""codex_bengalfox"": {
              ""limitId"": ""codex_bengalfox"",
              ""limitName"": ""GPT-5.3-Codex-Spark"",
              ""primary"": { ""usedPercent"": 4, ""resetsAt"": 1785269459, ""windowDurationMins"": 10080 },
              ""secondary"": null
            }
          },
          ""rateLimitResetCredits"": { ""availableCount"": 0, ""credits"": [] }
        }";

        using var doc = JsonDocument.Parse(sample);
        var usage = FromJsonResult(doc.RootElement);
        return usage.Rows.Count == 3
            && NearlyEquals(usage.OverallRemainingPercent, 87.5)
            && RowMatches(usage.Rows[0], "1w", 87.5, isPrimary: true)
            && RowMatches(usage.Rows[1], "Spark 1w", 96, isPrimary: false)
            && usage.Rows[2] is { Label: "Credits", Window: null, IsPrimary: false, DetailText: "146.09" };
    }

    private static bool RowMatches(UsageRow row, string label, double remainingPercent, bool isPrimary)
    {
        return row.Label == label
            && row.IsPrimary == isPrimary
            && row.Window is not null
            && NearlyEquals(row.Window.RemainingPercent, remainingPercent);
    }

    private static bool NearlyEquals(double actual, double expected) => Math.Abs(actual - expected) < 0.001;
}
