using System;
using System.Globalization;

namespace QuotaScope.Providers;

// Credits are the one row the providers do not hand over as a percentage.
// Codex reports a bare remaining balance and Claude reports credits already
// spent, neither with a ceiling attached, while the rest of the model speaks
// usedPercent. The configured full amount supplies the missing denominator so
// a credits row can render as a gauge like every other row.
internal static class CreditsGauge
{
    public const double DefaultFullAmount = 2500d;

    // Zero is a legitimate setting meaning "do not draw a gauge", and a
    // hand-edited settings file can hold anything, so callers ask before
    // dividing. IsFinite matters: NaN slips past a plain "> 0" test and would
    // poison the percent all the way to the progress bar.
    public static bool HasUsableCeiling(double fullAmount) =>
        double.IsFinite(fullAmount) && fullAmount > 0;

    // Codex: the payload is what is left, so a full wallet is 0% used.
    public static double UsedPercentFromBalance(double remaining, double fullAmount)
    {
        if (!HasUsableCeiling(fullAmount) || !double.IsFinite(remaining)) return 0d;
        return Math.Clamp((1d - remaining / fullAmount) * 100d, 0d, 100d);
    }

    // Claude: the payload is what has been consumed.
    public static double UsedPercentFromSpend(double spent, double fullAmount)
    {
        if (!HasUsableCeiling(fullAmount) || !double.IsFinite(spent)) return 0d;
        return Math.Clamp(spent / fullAmount * 100d, 0d, 100d);
    }

    // Both providers render as "left / full" so the two never read as opposites
    // sitting next to each other in the same popup.
    public static string FormatRemaining(double remaining, double fullAmount)
    {
        var left = Math.Max(0d, remaining);
        return string.Create(CultureInfo.InvariantCulture, $"{left:0.##} / {fullAmount:0.##}");
    }
}
