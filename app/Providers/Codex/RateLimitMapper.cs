using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    // Only consulted when a read does not name its main bucket; this is the id
    // the backend has used for it in every payload seen so far.
    private const string MainLimitId = "codex";

    public static ProviderUsage FromJsonResult(JsonElement result, double creditsFullAmount)
    {
        var mainSnapshotElement = result.TryGetProperty("rateLimits", out var direct)
            ? direct
            : result;
        var mainSnapshot = ParseSnapshot(mainSnapshotElement);
        var sparkSnapshot = TryFindSparkSnapshot(result);
        return ToProviderUsage(mainSnapshot, creditsFullAmount, sparkSnapshot);
    }

    // account/rateLimits/updated is a sparse rolling update, not a snapshot: it
    // carries one bucket, and a null or absent field means "not in this update",
    // never "cleared". Mapped on its own it would drop every window, Spark row,
    // and credits balance it leaves out, so it is folded into the last full read
    // instead. Returns a read-shaped result that FromJsonResult maps as usual.
    public static JsonElement MergeRollingUpdate(JsonElement lastRead, JsonElement notificationParams)
    {
        if (notificationParams.ValueKind != JsonValueKind.Object
            || !notificationParams.TryGetProperty("rateLimits", out var update)
            || update.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Rolling update carries no rateLimits snapshot.", nameof(notificationParams));
        }

        var merged = JsonNode.Parse(lastRead.GetRawText())!.AsObject();
        // The update names its bucket by limitId; none means the main bucket.
        // The main bucket is also mirrored in rateLimitsByLimitId, and both
        // copies have to move together.
        var mainId = GetNodeString((merged["rateLimits"] as JsonObject)?["limitId"]) ?? MainLimitId;
        var updateId = GetString(update, "limitId");
        if (updateId is null || updateId == mainId)
        {
            MergeSnapshot(GetOrAddObject(merged, "rateLimits"), update);
            if (merged["rateLimitsByLimitId"] is JsonObject byLimitId && byLimitId[mainId] is JsonObject mainEntry)
            {
                MergeSnapshot(mainEntry, update);
            }
        }
        else
        {
            MergeSnapshot(GetOrAddObject(GetOrAddObject(merged, "rateLimitsByLimitId"), updateId), update);
        }

        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void MergeSnapshot(JsonObject target, JsonElement update)
    {
        var slots = AssignWindowSlots(target, update);
        foreach (var property in update.EnumerateObject())
        {
            if (!slots.TryGetValue(property.Name, out var slot))
            {
                MergeValue(target, property.Name, property.Value);
                continue;
            }

            // A window of a different length is a different window: merged
            // field by field it would keep the old window's reset time, so it
            // replaces the stored one. No reset time beats a wrong one.
            var mins = GetLong(property.Value, "windowDurationMins");
            var storedMins = GetNodeLong((target[slot] as JsonObject)?["windowDurationMins"]);
            if (mins.HasValue && storedMins.HasValue && mins != storedMins)
            {
                target[slot] = JsonNode.Parse(property.Value.GetRawText());
            }
            else
            {
                MergeValue(target, slot, property.Value);
            }
        }
    }

    // A window that names the other slot's duration belongs to that slot.
    // Merging by slot alone would turn a 5h + 7d pair into two 7d rows if an
    // update fills the slots differently from the read. Both windows are placed
    // against the read before either is merged, and never into the same slot:
    // a duration match outranks a window's own slot name, and the window that
    // loses a slot takes the one left free. Property order cannot change it.
    private static Dictionary<string, string> AssignWindowSlots(JsonObject target, JsonElement update)
    {
        var storedMins = new Dictionary<string, long?>
        {
            ["primary"] = GetNodeLong((target["primary"] as JsonObject)?["windowDurationMins"]),
            ["secondary"] = GetNodeLong((target["secondary"] as JsonObject)?["windowDurationMins"])
        };

        var claims = new List<(string Name, string Slot, bool ByDuration)>();
        foreach (var (name, other) in new[] { ("primary", "secondary"), ("secondary", "primary") })
        {
            if (!update.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var mins = GetLong(window, "windowDurationMins");
            if (mins.HasValue && mins == storedMins[name]) claims.Add((name, name, true));
            else if (mins.HasValue && mins == storedMins[other]) claims.Add((name, other, true));
            else claims.Add((name, name, false));
        }

        if (claims.Count == 2 && claims[0].Slot == claims[1].Slot)
        {
            var loser = claims[0].ByDuration != claims[1].ByDuration
                ? (claims[0].ByDuration ? 1 : 0)
                : (claims[0].Name == claims[0].Slot ? 1 : 0);
            var freeSlot = claims[loser].Slot == "primary" ? "secondary" : "primary";
            claims[loser] = claims[loser] with { Slot = freeSlot };
        }

        return claims.ToDictionary(claim => claim.Name, claim => claim.Slot);
    }

    // Field-wise: objects (windows, credits, individualLimit) merge one field at
    // a time, so a window update without resetsAt keeps the known reset time.
    private static void MergeValue(JsonObject target, string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return;
            case JsonValueKind.Object:
                var child = GetOrAddObject(target, name);
                foreach (var property in value.EnumerateObject())
                {
                    MergeValue(child, property.Name, property.Value);
                }
                return;
            default:
                target[name] = JsonNode.Parse(value.GetRawText());
                return;
        }
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string? GetNodeString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? GetNodeLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    public static ProviderUsage ToProviderUsage(
        RateLimitSnapshot snapshot, double creditsFullAmount, RateLimitSnapshot? sparkSnapshot = null)
    {
        var rows = new List<UsageRow>();

        // Rows are payload-driven: only windows that actually exist become rows.
        // A plan whose windows are all a day or longer (Pro: weekly only) names
        // its rows after the plan, so the missing 5h row reads as how that plan
        // works rather than as data that failed to arrive. Plans with a 5h
        // window read like Claude's main rows, with no qualifier.
        var mainScope = IsWeeklyOnly(snapshot) ? PlanDisplayName(snapshot.PlanType) : null;
        foreach (var window in ShortestWindowFirst(snapshot))
        {
            AddWindowRow(rows, window, label => label, isPrimary: true, mainScope);
        }
        if (sparkSnapshot is not null)
        {
            foreach (var window in ShortestWindowFirst(sparkSnapshot))
            {
                AddWindowRow(rows, window, label => $"Spark {label}", isPrimary: false);
            }
        }

        if (snapshot.Credits is { HasCredits: true } credits)
        {
            rows.Add(BuildCreditsRow(credits, creditsFullAmount));
        }

        return new ProviderUsage(
            ProviderId,
            ProviderDisplayName,
            rows,
            ComputeOverallUsed(snapshot),
            string.IsNullOrWhiteSpace(snapshot.RateLimitReachedType) ? "Codex rate limit" : snapshot.RateLimitReachedType!,
            DateTimeOffset.Now,
            ProviderState.Ok);
    }

    private static void AddWindowRow(
        List<UsageRow> rows, RateLimitWindow? window, Func<string, string> labelFactory, bool isPrimary, string? scope = null)
    {
        if (window is null) return;
        rows.Add(new UsageRow(labelFactory(FormatDurationLabel(window.WindowDurationMins)), window, isPrimary, Scope: scope));
    }

    // Every present window must be known to span a day or more: a window
    // without a duration says nothing about the plan, and naming the rows
    // after it would mislabel a 5h window that merely arrived undated.
    private static bool IsWeeklyOnly(RateLimitSnapshot snapshot)
    {
        var present = new[] { snapshot.Primary, snapshot.Secondary }.Where(window => window is not null).ToArray();
        return present.Length > 0 && present.All(window => window!.WindowDurationMins is >= 1440);
    }

    // planType values from the app-server schema. Only plans whose display name
    // is certain are listed; anything else (including "unknown") gets no
    // qualifier rather than a guessed name.
    private static string? PlanDisplayName(string? planType) => planType?.ToLowerInvariant() switch
    {
        "free" => "Free",
        "go" => "Go",
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        _ => null
    };

    // Which slot holds which window is the backend's call (Pro puts its weekly
    // window in primary), so the 5-hour row is kept ahead of the weekly one by
    // duration rather than by slot, the same 5h-then-7d order Claude shows.
    // OrderBy is stable, so equal or unknown durations keep their slot order.
    private static IEnumerable<RateLimitWindow?> ShortestWindowFirst(RateLimitSnapshot snapshot)
    {
        return new[] { snapshot.Primary, snapshot.Secondary }
            .OrderBy(window => window?.WindowDurationMins ?? long.MaxValue);
    }

    // Unified label units across providers: hours under a day, days above (10080 -> "7d").
    public static string FormatDurationLabel(long? durationMins)
    {
        if (!durationMins.HasValue || durationMins.Value <= 0) return "usage";
        var mins = durationMins.Value;
        if (mins % 1440 == 0) return $"{mins / 1440}d";
        if (mins % 60 == 0) return $"{mins / 60}h";
        return $"{mins}m";
    }

    private static double ComputeOverallUsed(RateLimitSnapshot snapshot)
    {
        var max = 0d;
        foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })
        {
            if (window is null) continue;
            max = Math.Max(max, window.UsedPercent);
        }
        return max;
    }

    // The balance is what is left and carries no ceiling, so the gauge needs the
    // configured full amount. Unlimited credits have no meaningful fill, and a
    // balance that will not parse has no number to draw: both stay text-only.
    // A parsed balance still carries its amount, which alerts compare against
    // with or without a gauge.
    private static UsageRow BuildCreditsRow(CodexCredits credits, double fullAmount)
    {
        var parsed = decimal.TryParse(credits.Balance, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance);
        if (credits.Unlimited || !CreditsGauge.HasUsableCeiling(fullAmount) || !parsed)
        {
            return new UsageRow("Credits", null, IsPrimary: false, FormatCredits(credits),
                CreditsLeft: !credits.Unlimited && parsed ? (double)balance : null);
        }

        var remaining = (double)balance;
        return new UsageRow(
            "Credits",
            new RateLimitWindow(CreditsGauge.UsedPercentFromBalance(remaining, fullAmount), null, null),
            IsPrimary: false,
            CreditsGauge.FormatRemaining(remaining, fullAmount),
            BeyondFull: CreditsGauge.BalanceBeyondFull(remaining, fullAmount),
            CreditsLeft: remaining);
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

    // TryGetDouble/TryGetInt64 throw rather than returning false when the
    // element is not a number, and this payload spells "absent" as an explicit
    // null, so the kind has to be checked first.
    private static double? GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed))
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
        return UsageFixtures.Run("codex", MapFixture) && RunMainBucketMirrorSelfTest();
    }

    // A fixture is one full read plus the rolling updates merged into it, in
    // order, the way CodexUsageProvider sees them.
    internal static ProviderUsage MapFixture(JsonElement input, double creditsFullAmount)
    {
        var merged = input.GetProperty("read");
        if (input.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in updates.EnumerateArray())
            {
                merged = MergeRollingUpdate(merged, update);
            }
        }
        return FromJsonResult(merged, creditsFullAmount);
    }

    // The main bucket is mirrored in rateLimitsByLimitId and an update has to
    // move both copies. The mirror never becomes a row, so the fixtures cannot
    // see it.
    private static bool RunMainBucketMirrorSelfTest()
    {
        var read = ParseForTest(@"
            { ""rateLimits"": { ""limitId"": ""codex"", ""primary"": { ""usedPercent"": 20, ""windowDurationMins"": 300 } },
              ""rateLimitsByLimitId"": { ""codex"": { ""limitId"": ""codex"", ""primary"": { ""usedPercent"": 20, ""windowDurationMins"": 300 } } } }");
        var merged = MergeRollingUpdate(read, ParseForTest(@"
            { ""rateLimits"": { ""limitId"": ""codex"", ""primary"": { ""usedPercent"": 25, ""windowDurationMins"": 300 } } }"));
        return GetDouble(merged.GetProperty("rateLimitsByLimitId").GetProperty("codex").GetProperty("primary"), "usedPercent") == 25;
    }

    private static JsonElement ParseForTest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
