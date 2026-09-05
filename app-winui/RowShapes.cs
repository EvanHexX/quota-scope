using System;
using System.Collections.Generic;
using System.Linq;
using QuotaScope.Providers;

namespace QuotaScope.WinUI;

// A row the user can show, order, and assign a shape to, surfaced by the
// settings window. IsPrimary carries the provider's own default so settings can
// resolve visibility for a row it has never seen before.
internal sealed record UsageRowRef(string ProviderId, string ProviderName, string Label, bool IsPrimary);

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

    // Pure-logic self-test for row visibility: provider defaults first, then an
    // explicit per-row choice overriding them in both directions.
    public static bool RunSelfTest()
    {
        var settings = new AppSettings();
        var codex = settings.GetProvider("codex");
        var window = new RateLimitWindow(10d, null, 300);
        var main = new UsageRow("5h", window, IsPrimary: true);
        var spark5h = new UsageRow("Spark 5h", window, IsPrimary: false);
        var sparkWeekly = new UsageRow("Spark 7d", window, IsPrimary: false);
        // Credits now arrive with a window, which must not push the row into the
        // secondary-rows bucket.
        var credits = new UsageRow("Credits", window, IsPrimary: false, "12 / 2500");

        codex.ShowSecondaryRows = false;
        codex.ShowCredits = false;
        if (!IsVisible(settings, "codex", main)) return false;
        if (IsVisible(settings, "codex", spark5h) || IsVisible(settings, "codex", credits)) return false;

        codex.ShowSecondaryRows = true;
        codex.ShowCredits = true;
        if (!IsVisible(settings, "codex", spark5h) || !IsVisible(settings, "codex", credits)) return false;

        settings.RowVisibility[Key("codex", main.Label)] = false;
        settings.RowVisibility[Key("codex", spark5h.Label)] = false;
        if (IsVisible(settings, "codex", main) || IsVisible(settings, "codex", spark5h)) return false;

        codex.ShowSecondaryRows = false;
        settings.RowVisibility[Key("codex", sparkWeekly.Label)] = true;
        return IsVisible(settings, "codex", sparkWeekly);
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
