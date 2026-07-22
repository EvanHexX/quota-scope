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
    // App theme model: Dark / Light / Midnight, with glassmorphism as an
    // orthogonal material switch (translucent cards + acrylic backdrop).
    public static PopupPalette FromSettings(string? theme, bool glassmorphism)
    {
        var palette = theme?.ToUpperInvariant() switch
        {
            "LIGHT" => Light,
            "MIDNIGHT" => Midnight,
            _ => DarkBluePurple
        };
        return glassmorphism ? palette.WithGlass() : palette;
    }

    // The acrylic backdrop provides the tinted surface, so the outer card stays
    // nearly clear and only the row cards keep a light fill for separation.
    // Card's RGB is still used as the acrylic tint and the DWM border color.
    private PopupPalette WithGlass() => this with
    {
        Card = Color.FromArgb(0x14, Card.R, Card.G, Card.B),
        Row = Color.FromArgb(0x4D, Row.R, Row.G, Row.B),
        Track = Color.FromArgb(0x66, Track.R, Track.G, Track.B)
    };

    public static PopupPalette Light => new(
        Color.FromArgb(242, 245, 247, 251),
        Color.FromArgb(255, 255, 255, 255),
        Color.FromArgb(255, 201, 210, 228),
        Color.FromArgb(255, 225, 230, 240),
        Color.FromArgb(255, 27, 36, 48),
        Color.FromArgb(255, 90, 100, 120),
        Color.FromArgb(255, 46, 124, 214),
        Color.FromArgb(255, 122, 92, 224));

    public static PopupPalette DarkBluePurple => new(
        Color.FromArgb(242, 24, 26, 46),
        Color.FromArgb(255, 31, 34, 60),
        Color.FromArgb(255, 75, 82, 132),
        Color.FromArgb(255, 58, 63, 98),
        Color.FromArgb(255, 244, 247, 255),
        Color.FromArgb(255, 171, 181, 214),
        Color.FromArgb(255, 68, 154, 255),
        Color.FromArgb(255, 156, 104, 255));

    // Midnight is deliberately dim end-to-end: near-black surfaces with muted
    // text and desaturated accents so nothing glows in a dark room.
    public static PopupPalette Midnight => new(
        Color.FromArgb(242, 0, 0, 0),
        Color.FromArgb(255, 10, 10, 12),
        Color.FromArgb(255, 34, 34, 40),
        Color.FromArgb(255, 24, 24, 28),
        Color.FromArgb(255, 168, 172, 180),
        Color.FromArgb(255, 105, 109, 118),
        Color.FromArgb(255, 47, 111, 191),
        Color.FromArgb(255, 104, 92, 178));

    public Color AccentFor(int usedPercent)
    {
        if (usedPercent >= 80) return Color.FromArgb(255, 255, 126, 91);
        if (usedPercent >= 50) return Color.FromArgb(255, 132, 124, 255);
        return AccentBlue;
    }
}
