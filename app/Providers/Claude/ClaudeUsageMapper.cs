using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace QuotaScope.Providers.Claude;

internal static class ClaudeUsageMapper
{
    public const string ProviderId = "claude";
    public const string ProviderDisplayName = "Claude";

    public static ProviderUsage FromJson(JsonElement root)
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
        AddExtraUsageRow(rows, root);

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

    private static void AddExtraUsageRow(List<UsageRow> rows, JsonElement root)
    {
        var extra = GetObject(root, "extra_usage");
        if (extra is null || !(extra.Value.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True))
        {
            return;
        }

        var utilization = GetDouble(extra.Value, "utilization");
        if (utilization.HasValue)
        {
            rows.Add(new UsageRow("Credits", new RateLimitWindow(Math.Clamp(utilization.Value, 0d, 100d), null, null), IsPrimary: false));
            return;
        }

        var used = GetDouble(extra.Value, "used_credits");
        var limit = GetDouble(extra.Value, "monthly_limit");
        var detail = used.HasValue && limit.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{used.Value:0.##} / {limit.Value:0.##}")
            : "enabled";
        rows.Add(new UsageRow("Credits", null, IsPrimary: false, detail));
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

    private static double? GetDouble(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed))
        {
            return parsed;
        }
        return null;
    }

    public static bool RunSelfTest()
    {
        return RunBasicSelfTest() && RunFullRowsSelfTest() && RunLimitsArraySelfTest();
    }

    // Condensed from a live 2026-07-22 response: legacy per-model fields are
    // null and the data lives in the "limits" array (Fable = active
    // weekly_scoped). The legacy top-level windows must not duplicate rows.
    private static bool RunLimitsArraySelfTest()
    {
        const string sample = @"
        {
          ""five_hour"": { ""utilization"": 20.0, ""resets_at"": ""2026-07-22T17:19:59.607128+00:00"" },
          ""seven_day"": { ""utilization"": 28.0, ""resets_at"": ""2026-07-22T19:59:59.607147+00:00"" },
          ""seven_day_opus"": null,
          ""seven_day_sonnet"": null,
          ""limits"": [
            { ""kind"": ""session"", ""group"": ""session"", ""percent"": 20, ""severity"": ""normal"", ""resets_at"": ""2026-07-22T17:19:59.607128+00:00"", ""scope"": null, ""is_active"": false },
            { ""kind"": ""weekly_all"", ""group"": ""weekly"", ""percent"": 28, ""severity"": ""normal"", ""resets_at"": ""2026-07-22T19:59:59.607147+00:00"", ""scope"": null, ""is_active"": false },
            { ""kind"": ""weekly_scoped"", ""group"": ""weekly"", ""percent"": 55, ""severity"": ""normal"", ""resets_at"": ""2026-07-22T19:59:59.607376+00:00"", ""scope"": { ""model"": { ""id"": null, ""display_name"": ""Fable"" }, ""surface"": null }, ""is_active"": true },
            { ""kind"": ""weekly_scoped"", ""group"": ""weekly"", ""percent"": 10, ""severity"": ""normal"", ""resets_at"": ""2026-07-22T19:59:59.607376+00:00"", ""scope"": { ""model"": { ""id"": ""sonnet"", ""display_name"": ""Sonnet"" }, ""surface"": null }, ""is_active"": false }
          ],
          ""extra_usage"": { ""is_enabled"": false, ""monthly_limit"": null, ""used_credits"": null, ""utilization"": null }
        }";

        using var doc = JsonDocument.Parse(sample);
        var usage = FromJson(doc.RootElement);
        return usage.Rows.Count == 4
            && NearlyEquals(usage.OverallUsedPercent, 55)
            && RowMatches(usage.Rows[0], "5h", 20, isPrimary: true)
            && RowMatches(usage.Rows[1], "7d", 28, isPrimary: true)
            && RowMatches(usage.Rows[2], "7d Fable", 55, isPrimary: true)
            && RowMatches(usage.Rows[3], "7d Sonnet", 10, isPrimary: false)
            && usage.Rows[2].Window!.ResetsAt is not null;
    }

    // Sample shape from the undocumented oauth/usage endpoint: opus window null,
    // extra usage disabled.
    private static bool RunBasicSelfTest()
    {
        const string sample = @"
        {
          ""five_hour"":        { ""utilization"": 33.0, ""resets_at"": ""2026-04-11T07:00:00.528743+00:00"" },
          ""seven_day"":        { ""utilization"": 13.0, ""resets_at"": ""2026-04-17T00:59:59.951713+00:00"" },
          ""seven_day_opus"":   null,
          ""seven_day_sonnet"": { ""utilization"": 1.0,  ""resets_at"": ""2026-04-17T00:59:59.951713+00:00"" },
          ""extra_usage"":      { ""is_enabled"": false, ""monthly_limit"": null, ""used_credits"": null, ""utilization"": null }
        }";

        using var doc = JsonDocument.Parse(sample);
        var usage = FromJson(doc.RootElement);
        return usage.Rows.Count == 3
            && NearlyEquals(usage.OverallUsedPercent, 33)
            && RowMatches(usage.Rows[0], "5h", 33, isPrimary: true)
            && RowMatches(usage.Rows[1], "7d", 13, isPrimary: true)
            && RowMatches(usage.Rows[2], "7d Sonnet", 1, isPrimary: false)
            && usage.Rows[0].Window!.ResetsAt is { } resetsAt
            && resetsAt.UtcDateTime.Hour == 7;
    }

    private static bool RunFullRowsSelfTest()
    {
        const string sample = @"
        {
          ""five_hour"":        { ""utilization"": 37.5, ""resets_at"": ""2026-04-11T07:00:00+00:00"" },
          ""seven_day"":        { ""utilization"": 23.5, ""resets_at"": ""2026-04-17T00:59:59+00:00"" },
          ""seven_day_opus"":   { ""utilization"": 50.0, ""resets_at"": ""2026-04-17T00:59:59+00:00"" },
          ""seven_day_sonnet"": { ""utilization"": 1.0,  ""resets_at"": ""2026-04-17T00:59:59+00:00"" },
          ""seven_day_fable"":  { ""utilization"": 12.5, ""resets_at"": ""2026-04-17T00:59:59+00:00"" },
          ""extra_usage"":      { ""is_enabled"": true, ""monthly_limit"": 100, ""used_credits"": 25.5, ""utilization"": 25.5 }
        }";

        using var doc = JsonDocument.Parse(sample);
        var usage = FromJson(doc.RootElement);
        return usage.Rows.Count == 6
            && NearlyEquals(usage.OverallUsedPercent, 37.5)
            && RowMatches(usage.Rows[0], "5h", 37.5, isPrimary: true)
            && RowMatches(usage.Rows[1], "7d", 23.5, isPrimary: true)
            && RowMatches(usage.Rows[2], "7d Opus", 50, isPrimary: false)
            && RowMatches(usage.Rows[3], "7d Sonnet", 1, isPrimary: false)
            && RowMatches(usage.Rows[4], "7d Fable", 12.5, isPrimary: false)
            && RowMatches(usage.Rows[5], "Credits", 25.5, isPrimary: false);
    }

    private static bool RowMatches(UsageRow row, string label, double usedPercent, bool isPrimary)
    {
        return row.Label == label
            && row.IsPrimary == isPrimary
            && row.Window is not null
            && NearlyEquals(row.Window.UsedPercent, usedPercent);
    }

    private static bool NearlyEquals(double actual, double expected) => Math.Abs(actual - expected) < 0.001;
}
