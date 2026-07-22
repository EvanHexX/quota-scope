using System;

namespace QuotaScope.WinUI.Windows;

// How aggressively glassmorphism lets the desktop through. Higher strength =
// clearer panes (lower surface alpha) and a thinner acrylic tint/luminosity.
internal sealed record GlassStrength(
    string Id,
    byte CardAlpha,
    byte RowAlpha,
    byte TrackAlpha,
    byte EdgeAlpha,
    float TintOpacity,
    float LuminosityOpacityDark,
    float LuminosityOpacityLight)
{
    public static readonly GlassStrength Subtle = new("Subtle", 0x2E, 0x73, 0x8C, 0x2E, 0.32f, 0.88f, 0.92f);
    public static readonly GlassStrength Medium = new("Medium", 0x14, 0x4D, 0x66, 0x3D, 0.20f, 0.70f, 0.80f);
    public static readonly GlassStrength Strong = new("Strong", 0x00, 0x2E, 0x40, 0x52, 0.10f, 0.42f, 0.55f);

    public static GlassStrength Parse(string? value) => value?.ToUpperInvariant() switch
    {
        "SUBTLE" => Subtle,
        "STRONG" => Strong,
        _ => Medium
    };

    public float LuminosityOpacity(bool isDark) => isDark ? LuminosityOpacityDark : LuminosityOpacityLight;
}
