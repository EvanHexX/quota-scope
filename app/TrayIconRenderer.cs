using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace QuotaScope;

// Tray icon signal is deliberately limited to two channels: arc fill ratio
// (usedPercent quantized to 5% steps) and a 3-level state color. Exact numbers
// live in the tooltip and popup.
internal enum TrayIconState
{
    Normal,
    Warning,
    Critical
}

internal static class TrayIconRenderer
{
    public static Icon CreateAppIcon()
    {
        try
        {
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawAppMark(g, Color.FromArgb(73, 169, 255), Color.FromArgb(154, 116, 255));
            return CreateIconFromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    // DPI-native icon sizes; the shell no longer downscales a fixed 32x32 render.
    public static int GetNativeIconSize()
    {
        try
        {
            return GetNativeIconSize(GetDpiForSystem());
        }
        catch
        {
            return 16;
        }
    }

    public static int GetNativeIconSize(uint dpi) => dpi switch
    {
        >= 168 => 32,
        >= 132 => 24,
        >= 108 => 20,
        _ => 16
    };

    public static TrayIconState ComputeState(double overallUsedPercent, int warningThresholdRemainingPercent, bool anyRateLimited)
    {
        if (anyRateLimited || overallUsedPercent >= 97.5) return TrayIconState.Critical;
        var warningAtUsed = 100 - Math.Clamp(warningThresholdRemainingPercent, 0, 100);
        return overallUsedPercent >= warningAtUsed ? TrayIconState.Warning : TrayIconState.Normal;
    }

    // Fallback style when the arc reads poorly at small sizes: a plain filled
    // dot carrying only the state color.
    public static Icon CreateGlyphIcon(TrayIconState state, int sizePx)
    {
        try
        {
            using var bitmap = new Bitmap(sizePx, sizePx);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var color = StateColor(state);
            var inset = sizePx >= 24 ? 3f : 2f;
            using var fill = new SolidBrush(color);
            using var border = new Pen(Color.FromArgb(110, 20, 24, 34), sizePx >= 24 ? 1.6f : 1.2f);
            g.FillEllipse(fill, inset, inset, sizePx - inset * 2, sizePx - inset * 2);
            g.DrawEllipse(border, inset, inset, sizePx - inset * 2, sizePx - inset * 2);
            return CreateIconFromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    public static Icon CreateUsageIcon(double fillPercent, TrayIconState state, int sizePx)
    {
        try
        {
            var quantized = Math.Clamp(Math.Round(fillPercent / 5d) * 5d, 0d, 100d);
            using var bitmap = new Bitmap(sizePx, sizePx);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var stroke = sizePx switch
            {
                >= 32 => 4.0f,
                >= 24 => 3.2f,
                >= 20 => 2.6f,
                _ => 2.2f
            };
            var inset = stroke / 2f + (sizePx >= 24 ? 2f : 1f);
            var rect = new RectangleF(inset, inset, sizePx - inset * 2, sizePx - inset * 2);

            var color = StateColor(state);

            using var track = new Pen(Color.FromArgb(96, 178, 188, 205), stroke);
            g.DrawEllipse(track, rect.X, rect.Y, rect.Width, rect.Height);
            if (quantized > 0)
            {
                using var arc = new Pen(color, stroke)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };
                g.DrawArc(arc, rect.X, rect.Y, rect.Width, rect.Height, -90, (float)(360 * quantized / 100));
            }

            return CreateIconFromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    private static Color StateColor(TrayIconState state) => state switch
    {
        TrayIconState.Critical => Color.FromArgb(235, 68, 56),
        TrayIconState.Warning => Color.FromArgb(255, 179, 64),
        _ => Color.FromArgb(73, 169, 255)
    };

    private static void DrawAppMark(Graphics g, Color blue, Color violet)
    {
        var outer = new Rectangle(3, 3, 26, 26);
        using var background = new SolidBrush(Color.FromArgb(10, 12, 20));
        using var border = new Pen(Color.FromArgb(214, 226, 244), 1.6f);
        using var bluePen = new Pen(blue, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var violetPen = new Pen(violet, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.FillEllipse(background, outer);
        g.DrawEllipse(border, outer);
        g.DrawArc(bluePen, outer.X + 5, outer.Y + 5, outer.Width - 10, outer.Height - 10, -145, 205);
        g.DrawArc(violetPen, outer.X + 8, outer.Y + 8, outer.Width - 16, outer.Height - 16, 35, 240);
    }

    private static Icon CreateIconFromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
