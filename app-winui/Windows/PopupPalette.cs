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
            "MIDNIGHT" => MidnightBlack,
            _ => DarkBluePurple
        };
        return glassmorphism ? palette.WithGlass() : palette;
    }

    private PopupPalette WithGlass() => this with
    {
        Card = Color.FromArgb(0xCC, Card.R, Card.G, Card.B),
        Row = Color.FromArgb(0xD9, Row.R, Row.G, Row.B)
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

    public static PopupPalette MidnightBlack => new(
        Color.FromArgb(242, 5, 5, 7),
        Color.FromArgb(255, 13, 13, 17),
        Color.FromArgb(255, 46, 46, 54),
        Color.FromArgb(255, 33, 33, 39),
        Color.FromArgb(255, 248, 248, 250),
        Color.FromArgb(255, 178, 180, 188),
        Color.FromArgb(255, 75, 180, 255),
        Color.FromArgb(255, 134, 116, 255));

    public Color AccentFor(int usedPercent)
    {
        if (usedPercent >= 80) return Color.FromArgb(255, 255, 126, 91);
        if (usedPercent >= 50) return Color.FromArgb(255, 132, 124, 255);
        return AccentBlue;
    }
}
