using System;
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
