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
    public static PopupPalette FromTheme(string? theme)
    {
        return theme?.ToUpperInvariant() switch
        {
            "MIDNIGHTBLACK" => MidnightBlack,
            "NEBULA" => Nebula,
            "GLASSMORPHISM" => Glassmorphism,
            _ => DarkBluePurple
        };
    }

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

    public static PopupPalette Nebula => new(
        Color.FromArgb(242, 34, 38, 66),
        Color.FromArgb(255, 42, 48, 82),
        Color.FromArgb(255, 112, 128, 194),
        Color.FromArgb(255, 52, 61, 96),
        Color.FromArgb(255, 248, 250, 255),
        Color.FromArgb(255, 188, 199, 229),
        Color.FromArgb(255, 74, 166, 255),
        Color.FromArgb(255, 182, 118, 255));

    // Glassmorphism pairs with a DesktopAcrylic backdrop, so its card is more translucent.
    public static PopupPalette Glassmorphism => new(
        Color.FromArgb(204, 30, 46, 66),
        Color.FromArgb(230, 39, 58, 80),
        Color.FromArgb(255, 146, 179, 214),
        Color.FromArgb(255, 62, 82, 108),
        Color.FromArgb(255, 252, 254, 255),
        Color.FromArgb(255, 206, 222, 238),
        Color.FromArgb(255, 92, 196, 255),
        Color.FromArgb(255, 176, 146, 255));

    public Color AccentFor(int remainingPercent)
    {
        if (remainingPercent <= 20) return Color.FromArgb(255, 255, 126, 91);
        if (remainingPercent <= 50) return Color.FromArgb(255, 132, 124, 255);
        return AccentBlue;
    }
}
