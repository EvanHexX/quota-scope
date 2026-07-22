using System;
using System.Collections.Generic;
using System.Linq;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// A row the user can assign a shape to, surfaced by the settings window.
internal sealed record UsageRowRef(string ProviderId, string ProviderName, string Label);

internal static class RowShapes
{
    public const string Circle = "Circle";
    public const string Bars = "Bars";

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

    public static string Resolve(AppSettings settings, string providerId, UsageRow row)
    {
        if (string.Equals(settings.ShapeTheme, "Bars", StringComparison.OrdinalIgnoreCase)) return Bars;
        if (string.Equals(settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase)) return Circle;

        // Mix & match: per-row override, gauge by default.
        return settings.RowShapes.TryGetValue(Key(providerId, row.Label), out var shape)
            && string.Equals(shape, Bars, StringComparison.OrdinalIgnoreCase)
                ? Bars
                : Circle;
    }
}
