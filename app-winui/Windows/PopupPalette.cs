using System;
using Windows.UI;

namespace QuotaScope.WinUI.Windows;

// Port of the WinForms PopupPalette records (app/UsagePopupForm.cs) to
// Windows.UI colors. The card fills the whole window, so the old standalone
// Background color is folded into Card. Card alpha is slightly below opaque so
// the Mica/Acrylic backdrop reads through.
internal sealed record PopupPalette(
    Color Card,
    Color Row,
    Color Border,
    Color Track,
    Color Text,
    Color Muted,
    Color AccentBlue,
    Color AccentPurple)
{
    // Fill cards tint their own background with the accent instead of drawing a
    // bar, and the row's text sits on that tint, so these alphas are capped by
    // contrast, not chosen by eye: RunSelfTest holds every theme and accent band
    // to it. The edge is the 2px band that marks where the fill stops; a glyph
    // only ever crosses it, so it is held to the looser 3:1.
    public byte FillAlpha { get; init; }
    public byte FillEdgeAlpha { get; init; }

    // App theme model: Dark / Light / Midnight, with glassmorphism as an
    // orthogonal material switch (translucent cards + acrylic backdrop).
    public static PopupPalette FromSettings(string? theme, bool glassmorphism, string? strength = null)
    {
        var palette = theme?.ToUpperInvariant() switch
        {
            "LIGHT" => Light,
            "MIDNIGHT" => Midnight,
            _ => DarkBluePurple
        };
        return glassmorphism ? palette.WithGlass(GlassStrength.Parse(strength)) : palette;
    }

    // The acrylic backdrop provides the tinted surface, so the outer card stays
    // nearly clear and the row cards keep just enough fill to read as panes.
    // Card's RGB is still used as the acrylic tint and the DWM border color.
    // The fill alphas carry over unchanged. What shows through a glass pane
    // cannot be measured, but the opaque Row is about the worst base for the
    // tint (darkest for the dark themes, white for Light): the share of contrast
    // RunSelfTest sees the fill take there is the most the same alpha can take
    // over glass. A stronger tint would also be the most opaque layer on a
    // clear pane and read as paint rather than glass.
    private PopupPalette WithGlass(GlassStrength strength) => this with
    {
        Card = Color.FromArgb(strength.CardAlpha, Card.R, Card.G, Card.B),
        Row = Color.FromArgb(strength.RowAlpha, Row.R, Row.G, Row.B),
        Track = Color.FromArgb(strength.TrackAlpha, Track.R, Track.G, Track.B)
    };

    public static PopupPalette Light => new(
        Color.FromArgb(242, 245, 247, 251),
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 201, 210, 228),
        Color.FromArgb(255, 225, 230, 240),
        Color.FromArgb(255, 27, 36, 48),
        Color.FromArgb(255, 90, 100, 120),
        Color.FromArgb(255, 46, 124, 214),
        Color.FromArgb(255, 122, 92, 224))
    {
        FillAlpha = 56,
        FillEdgeAlpha = 150
    };

    public static PopupPalette DarkBluePurple => new(
        Color.FromArgb(242, 24, 26, 46),
        Color.FromArgb(255, 31, 34, 60),
        Color.FromArgb(255, 75, 82, 132),
        Color.FromArgb(255, 58, 63, 98),
        Color.FromArgb(255, 244, 247, 255),
        Color.FromArgb(255, 171, 181, 214),
        Color.FromArgb(255, 68, 154, 255),
        Color.FromArgb(255, 156, 104, 255))
    {
        FillAlpha = 60,
        FillEdgeAlpha = 150
    };

    // Midnight is deliberately dim end-to-end: near-black surfaces with muted
    // text and desaturated accents so nothing glows in a dark room. Its muted
    // text starts under 4.5:1, so the fill may cost it only a tenth of that,
    // which leaves the tint as faint as the bar track; the edge carries the value.
    public static PopupPalette Midnight => new(
        Color.FromArgb(242, 0, 0, 0),
        Color.FromArgb(255, 10, 10, 12),
        Color.FromArgb(255, 34, 34, 40),
        Color.FromArgb(255, 24, 24, 28),
        Color.FromArgb(255, 168, 172, 180),
        Color.FromArgb(255, 105, 109, 118),
        Color.FromArgb(255, 47, 111, 191),
        Color.FromArgb(255, 104, 92, 178))
    {
        FillAlpha = 22,
        FillEdgeAlpha = 96
    };

    // Glass edge highlight: the bright 1px rim that makes a translucent pane
    // read as glass instead of a flat film.
    public Color GlassEdge(bool isDark, GlassStrength strength) => isDark
        ? Color.FromArgb(strength.EdgeAlpha, 255, 255, 255)
        : Color.FromArgb((byte)(strength.EdgeAlpha + 24), 255, 255, 255);

    public Color AccentFor(int usedPercent)
    {
        if (usedPercent >= 80) return Color.FromArgb(255, 255, 126, 91);
        if (usedPercent >= 50) return Color.FromArgb(255, 132, 124, 255);
        return AccentBlue;
    }

    // Same color rule as the bars: the band follows usage, whatever the card shows.
    public Color FillTint(int usedPercent) => WithAlpha(AccentFor(usedPercent), FillAlpha);

    public Color FillEdge(int usedPercent) => WithAlpha(AccentFor(usedPercent), FillEdgeAlpha);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // WCAG contrast of the row text over a filled card, for every opaque theme
    // and every accent band. Label and percent need 4.5:1 and the footer 3:1,
    // except where a theme's own muted text is already under 4.5:1 (Midnight):
    // there the fill may cost at most a tenth of the contrast the card had.
    public static bool RunSelfTest()
    {
        foreach (var palette in new[] { DarkBluePurple, Light, Midnight })
        {
            if (palette.FillAlpha == 0 || palette.FillEdgeAlpha <= palette.FillAlpha) return false;

            var unfilledMuted = ContrastRatio(palette.Muted, palette.Row);
            var mutedFloor = unfilledMuted < 4.5 ? 0.9 * unfilledMuted : 3.0;
            // One value from each side of every band boundary.
            foreach (var used in new[] { 0, 49, 50, 79, 80, 100 })
            {
                var filled = Over(palette.FillTint(used), palette.Row);
                var edge = Over(palette.FillEdge(used), palette.Row);
                if (ContrastRatio(palette.Text, filled) < 4.5) return false;
                if (ContrastRatio(palette.Muted, filled) < mutedFloor) return false;
                if (ContrastRatio(palette.Text, edge) < 3.0) return false;
            }
        }
        return true;
    }

    // Source-over onto an opaque base, per channel in sRGB space as the
    // compositor blends; kept unrounded so the ratios are not nudged by 8-bit steps.
    private static (double R, double G, double B) Over(Color top, Color opaqueBase)
    {
        var alpha = top.A / 255.0;
        return (
            top.R * alpha + opaqueBase.R * (1 - alpha),
            top.G * alpha + opaqueBase.G * (1 - alpha),
            top.B * alpha + opaqueBase.B * (1 - alpha));
    }

    private static double ContrastRatio(Color text, (double R, double G, double B) background) =>
        ContrastRatio(RelativeLuminance(text.R, text.G, text.B), RelativeLuminance(background.R, background.G, background.B));

    private static double ContrastRatio(Color text, Color opaqueBackground) =>
        ContrastRatio(text, (opaqueBackground.R, opaqueBackground.G, opaqueBackground.B));

    private static double ContrastRatio(double luminanceA, double luminanceB)
    {
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(double r, double g, double b) =>
        0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double Linear(double channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
