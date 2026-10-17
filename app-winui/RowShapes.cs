using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// A row the user can show, order, and assign a shape to, surfaced by the
// settings window. IsPrimary carries the provider's own default so settings can
// resolve visibility for a row it has never seen before. Scope only names the
// row (see UsageRow); the key stays provider id + Label.
internal sealed record UsageRowRef(string ProviderId, string ProviderName, string Label, bool IsPrimary, string? Scope = null);

internal static class RowShapes
{
    public const string Circle = "Circle";
    public const string Bars = "Bars";
    // Two-line card whose own background fills up to the percentage. Also the
    // ShapeTheme value that applies it to every row.
    public const string Fill = "Fill";

    // Stable per-row key: provider id + the provider's English row label.
    public static string Key(string providerId, string label) => $"{providerId}|{label}";

    // Rank used for the user-defined display order. Rows without an override
    // stay in payload order, after any row the user has pinned to a position.
    public static int Rank(AppSettings settings, string providerId, string label, int payloadIndex)
    {
        return settings.RowOrder.TryGetValue(Key(providerId, label), out var order)
            ? order
            : 1000 + payloadIndex;
    }

    public static List<T> Order<T>(AppSettings settings, string providerId, IEnumerable<T> rows, Func<T, string> labelOf)
    {
        return rows
            .Select((row, index) => (row, index))
            .OrderBy(item => Rank(settings, providerId, labelOf(item.row), item.index))
            .ThenBy(item => item.index)
            .Select(item => item.row)
            .ToList();
    }

    // The credits row carries a gauge like every other row now, so whether a row
    // has a window no longer separates credits from the model windows.
    public static bool IsCreditsRow(string label) =>
        label.Equals("Credits", StringComparison.OrdinalIgnoreCase);

    // Row visibility. The settings row list writes an explicit choice per row;
    // rows without one fall back to the provider defaults, i.e. the windows the
    // provider marks primary are shown and model/credit rows follow the
    // provider's own toggles.
    public static bool IsVisible(AppSettings settings, string providerId, string label, bool isPrimary)
    {
        if (settings.RowVisibility.TryGetValue(Key(providerId, label), out var visible)) return visible;
        if (isPrimary) return true;
        var provider = settings.GetProvider(providerId);
        return IsCreditsRow(label) ? provider.ShowCredits : provider.ShowSecondaryRows;
    }

    public static bool IsVisible(AppSettings settings, string providerId, UsageRow row) =>
        IsVisible(settings, providerId, row.Label, row.IsPrimary);

    public static bool IsVisible(AppSettings settings, UsageRowRef row) =>
        IsVisible(settings, row.ProviderId, row.Label, row.IsPrimary);

    // Samples live in fixtures/rows (docs/MACOS_BUILD.md, section 5): row
    // visibility (provider defaults, then an explicit per-row choice in both
    // directions) and the shape each row is drawn with.
    public static bool RunSelfTest() => UsageFixtures.Run("rows", EvaluateFixture);

    // Each row ({ providerId, label, isPrimary }) under the case's settings is
    // answered by its settings key, whether it is shown, and its shape.
    private static JsonNode EvaluateFixture(JsonElement input)
    {
        var settings = UsageFixtures.ReadSettings(UsageFixtures.Optional(input, "settings"));
        return new JsonObject
        {
            ["rows"] = new JsonArray(input.GetProperty("rows").EnumerateArray().Select(item =>
            {
                var providerId = item.GetProperty("providerId").GetString()!;
                var row = new UsageRow(item.GetProperty("label").GetString()!, null, item.GetProperty("isPrimary").GetBoolean());
                return (JsonNode)new JsonObject
                {
                    ["key"] = Key(providerId, row.Label),
                    ["visible"] = IsVisible(settings, providerId, row),
                    ["shape"] = Resolve(settings, providerId, row)
                };
            }).ToArray())
        };
    }

    public static string Resolve(AppSettings settings, string providerId, UsageRow row)
    {
        if (string.Equals(settings.ShapeTheme, "Bars", StringComparison.OrdinalIgnoreCase)) return Bars;
        if (string.Equals(settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase)) return Circle;
        if (string.Equals(settings.ShapeTheme, Fill, StringComparison.OrdinalIgnoreCase)) return Fill;

        // Mix & match: per-row override, gauge by default.
        if (!settings.RowShapes.TryGetValue(Key(providerId, row.Label), out var shape)) return Circle;
        if (string.Equals(shape, Bars, StringComparison.OrdinalIgnoreCase)) return Bars;
        return string.Equals(shape, Fill, StringComparison.OrdinalIgnoreCase) ? Fill : Circle;
    }
}
