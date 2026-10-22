using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace QuotaScope.Providers.Claude;

internal static class ClaudeUsageMapper
{
    public const string ProviderId = "claude";
    public const string ProviderDisplayName = "Claude";

    public static ProviderUsage FromJson(JsonElement root, double creditsFullAmount)
    {
        var rows = new List<UsageRow>();

        // Newer responses carry a "limits" array (session / weekly_all /
        // weekly_scoped with a model scope, e.g. Fable); when present it is the
        // authoritative source. Older field-based responses stay supported.
        if (!TryAddLimitsRows(rows, root))
        {
            AddWindowRow(rows, root, "five_hour", "5h", isPrimary: true);
            AddWindowRow(rows, root, "seven_day", "7d", isPrimary: true);
            AddPerModelRows(rows, root);
        }
        AddExtraUsageRow(rows, root, creditsFullAmount);

        return new ProviderUsage(
            ProviderId,
            ProviderDisplayName,
            rows,
            ComputeOverallUsed(rows),
            "Claude rate limit",
            DateTimeOffset.Now,
            ProviderState.Ok);
    }

    private static void AddWindowRow(List<UsageRow> rows, JsonElement root, string property, string label, bool isPrimary)
    {
        var window = ParseWindow(GetObject(root, property));
        if (window is null) return;
        rows.Add(new UsageRow(label, window, isPrimary));
    }

    private static bool TryAddLimitsRows(List<UsageRow> rows, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("limits", out var limits)
            || limits.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var added = false;
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object) continue;
            var percent = GetDouble(limit, "percent") ?? 0d;
            var window = new RateLimitWindow(Math.Clamp(percent, 0d, 100d), ParseResetsAt(limit), null);
            var group = GetString(limit, "group");
            var baseLabel = string.Equals(group, "session", StringComparison.OrdinalIgnoreCase) ? "5h" : "7d";
            var scopeName = ScopeName(limit);
            var isActive = limit.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.True;

            if (scopeName is null)
            {
                rows.Add(new UsageRow(baseLabel, window, IsPrimary: true));
            }
            else
            {
                // An active scoped limit is the constraint that currently
                // matters: always visible and counted into the overall number.
                rows.Add(new UsageRow($"{baseLabel} {scopeName}", window, IsPrimary: isActive));
            }
            added = true;
        }
        return added;
    }

    private static string? ScopeName(JsonElement limit)
    {
        if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(model, "display_name") ?? GetString(model, "id");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return GetString(scope, "surface") ?? "scoped";
    }

    // Per-model weekly windows (seven_day_sonnet/opus/fable/...) are mapped
    // generically so newly introduced models show up without a code change.
    private const string PerModelPrefix = "seven_day_";

    private static void AddPerModelRows(List<UsageRow> rows, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.StartsWith(PerModelPrefix, StringComparison.Ordinal)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var window = ParseWindow(property.Value);
            if (window is null) continue;
            rows.Add(new UsageRow($"7d {ModelLabel(property.Name[PerModelPrefix.Length..])}", window, IsPrimary: false));
        }
    }

    private static string ModelLabel(string rawName)
    {
        if (rawName.Length == 0) return rawName;
        var pretty = rawName.Replace('_', ' ');
        return char.ToUpperInvariant(pretty[0]) + pretty[1..];
    }

    // The row is always emitted. An account with extra usage switched off has
    // spent nothing, which is a real 0% rather than a row worth hiding -- and
    // hiding it is what the per-row visibility toggle is for.
    private static void AddExtraUsageRow(List<UsageRow> rows, JsonElement root, double fullAmount)
    {
        var extra = GetObject(root, "extra_usage");
        var utilization = extra is null ? null : GetDouble(extra.Value, "utilization");
        var spent = (extra is null ? null : GetDouble(extra.Value, "used_credits")) ?? 0d;
        var monthlyLimit = extra is null ? null : GetDouble(extra.Value, "monthly_limit");

        // Prefer what the provider reports -- an explicit utilization, then a
        // monthly limit to divide by -- and fall back to the configured amount.
        var ceiling = monthlyLimit is > 0 ? monthlyLimit.Value : fullAmount;
        var hasCeiling = CreditsGauge.HasUsableCeiling(ceiling);

        // An explicit utilization is already a percentage and stands on its own.
        // Without one there is nothing to divide by, so the row stays text-only
        // rather than drawing a gauge against a ceiling of zero.
        if (!utilization.HasValue && !hasCeiling)
        {
            rows.Add(new UsageRow("Credits", null, IsPrimary: false,
                spent > 0 ? string.Create(CultureInfo.InvariantCulture, $"{spent:0.##}") : "--"));
            return;
        }

        var usedPercent = utilization.HasValue
            ? Math.Clamp(utilization.Value, 0d, 100d)
            : CreditsGauge.UsedPercentFromSpend(spent, ceiling);

        // Derived from the percentage actually drawn, not from used_credits:
        // the payload can report a utilization with no used_credits at all, and
        // the two can disagree. A footer that contradicts its own bar is worse
        // than no footer.
        // What credits alerts compare against, and what the footer prints.
        double? left = hasCeiling ? ceiling * (1d - usedPercent / 100d) : null;
        var detail = left is { } amount ? CreditsGauge.FormatRemaining(amount, ceiling) : null;
        // Spend past the ceiling clamps the gauge full; the flag lets the
        // percent read >100% rather than claim the spend stopped at 100%.
        var beyondFull = utilization.HasValue
            ? utilization.Value > 100d
            : CreditsGauge.SpendBeyondFull(spent, ceiling);

        rows.Add(new UsageRow(
            "Credits", new RateLimitWindow(usedPercent, null, null), IsPrimary: false, detail,
            BeyondFull: beyondFull, CreditsLeft: left));
    }

    private static double ComputeOverallUsed(List<UsageRow> rows)
    {
        var max = 0d;
        foreach (var row in rows)
        {
            if (!row.IsPrimary || row.Window is null) continue;
            max = Math.Max(max, row.Window.UsedPercent);
        }
        return max;
    }

    private static RateLimitWindow? ParseWindow(JsonElement? element)
    {
        if (element is null) return null;
        var utilization = GetDouble(element.Value, "utilization") ?? 0d;
        return new RateLimitWindow(Math.Clamp(utilization, 0d, 100d), ParseResetsAt(element.Value), null);
    }

    // ISO 8601 UTC strings; deliberately not shared with the Codex epoch parser.
    private static DateTimeOffset? ParseResetsAt(JsonElement element)
    {
        if (!element.TryGetProperty("resets_at", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement? GetObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    // TryGetDouble throws rather than returning false when the element is not a
    // number, and this payload spells "absent" as an explicit null, so the kind
    // has to be checked first.
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

    public static bool RunSelfTest()
    {
        return UsageFixtures.Run("claude", MapFixture);
    }

    // A fixture is one usage endpoint response.
    internal static ProviderUsage MapFixture(JsonElement input, double creditsFullAmount)
    {
        return FromJson(input.GetProperty("response"), creditsFullAmount);
    }
}
