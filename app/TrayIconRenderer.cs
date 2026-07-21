using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace QuotaScope;

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

    public static Icon CreateUsageIcon(int remainingPercent, bool warning)
    {
        try
        {
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawAppMark(g, Color.FromArgb(73, 169, 255), Color.FromArgb(154, 116, 255));
            var color = warning ? Color.FromArgb(210, 72, 64) : Color.FromArgb(37, 131, 92);
            using var background = new Pen(Color.FromArgb(178, 188, 205), 3);
            using var progress = new Pen(color, 3) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawEllipse(background, 6, 6, 20, 20);
            g.DrawArc(progress, 6, 6, 20, 20, -90, 360 * Math.Clamp(remainingPercent, 0, 100) / 100f);
            using var textBrush = new SolidBrush(color);
            using var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
            var label = Math.Clamp(remainingPercent, 0, 99).ToString();
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, textBrush, (32 - size.Width) / 2, (32 - size.Height) / 2);
            return CreateIconFromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

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
}
