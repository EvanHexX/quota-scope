using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using QuotaScope.Providers;

namespace QuotaScope;

internal sealed class UsagePopupForm : Form
{
    private const int WmNchittest = 0x0084;
    private const int Htcaption = 2;
    private const int BarRowHeight = 54;
    private const int BarRowSpacing = 68;
    private const int BarTop = 86;
    private const int SectionHeaderHeight = 56;
    private const int BentoTop = 84;
    private const int BentoCardHeight = 230;
    private const int BentoRowGap = 14;
    private static readonly Color TransparentCanvas = Color.FromArgb(1, 2, 3);
    private static readonly string UiFontFamily = ResolveUiFontFamily();
    private readonly List<Rectangle> _timeToggleBounds = new();
    private IReadOnlyList<ProviderUsage> _usages = Array.Empty<ProviderUsage>();
    private AppSettings _settings;

    public event Action? SettingsChanged;

    public UsagePopupForm(AppSettings settings)
    {
        _settings = settings;
        AutoScaleMode = AutoScaleMode.Dpi;
        ApplyThemeSize();
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = _settings.IsPinned;
        BackColor = TransparentCanvas;
        TransparencyKey = TransparentCanvas;
        ForeColor = Color.FromArgb(244, 247, 255);
        Font = new Font(UiFontFamily, 10f, FontStyle.Regular, GraphicsUnit.Point);
        Text = "Usage";
        Icon = TrayIconRenderer.CreateAppIcon();
        DoubleBuffered = true;
        KeyPreview = true;
        Deactivate += (_, _) => { if (!_settings.IsPinned) Hide(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && !_settings.IsPinned) Hide(); };
        MouseClick += OnMouseClick;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        TopMost = _settings.IsPinned;
        ApplyThemeSize();
        Invalidate();
    }

    public void SetUsage(IReadOnlyList<ProviderUsage> usages)
    {
        _usages = usages;
        ApplyThemeSize();
        Invalidate();
    }

    private List<(ProviderUsage Usage, List<UsageRow> Rows)> BuildSections()
    {
        var sections = new List<(ProviderUsage, List<UsageRow>)>();
        foreach (var usage in _usages)
        {
            var provider = _settings.GetProvider(usage.ProviderId);
            var rows = usage.Rows.Where(row => IsRowVisible(row, provider)).ToList();
            sections.Add((usage, rows));
        }
        return sections;
    }

    private static bool IsRowVisible(UsageRow row, ProviderSettings provider)
    {
        if (row.IsPrimary) return true;
        return row.Window is not null ? provider.ShowSecondaryRows : provider.ShowCredits;
    }

    private void ApplyThemeSize()
    {
        var sections = BuildSections();
        var usesBento = string.Equals(_settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase);
        Size targetSize;
        if (usesBento)
        {
            var cardCount = sections.Sum(s => s.Rows.Count);
            var cardRows = Math.Max(1, (cardCount + 1) / 2);
            targetSize = new Size(452, BentoTop + cardRows * BentoCardHeight + (cardRows - 1) * BentoRowGap + 24);
        }
        else
        {
            var rowCount = sections.Sum(s => s.Rows.Count);
            var extraHeaders = Math.Max(0, sections.Count - 1) * SectionHeaderHeight;
            var height = BarTop + rowCount * BarRowSpacing + extraHeaders + 14;
            if (rowCount == 0) height = 236;
            targetSize = new Size(408, height);
        }

        MinimumSize = targetSize;
        ClientSize = targetSize;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmNchittest || (int)m.Result != 1)
        {
            return;
        }

        var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        if (GetPinBounds().Contains(point))
        {
            return;
        }

        if (GetDragHandleBounds().Contains(point))
        {
            m.Result = (IntPtr)Htcaption;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _timeToggleBounds.Clear();
        var g = e.Graphics;
        g.PageUnit = GraphicsUnit.Pixel;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = PopupPalette.FromTheme(_settings.ColorTheme);
        DrawShell(g, palette);

        using var titleFont = new Font(UiFontFamily, 13f, FontStyle.Bold, GraphicsUnit.Point);
        using var statusFont = new Font(UiFontFamily, 8.8f, FontStyle.Regular, GraphicsUnit.Point);

        var title = _usages.Count > 0 ? $"{_usages[0].DisplayName} Usage" : "Usage";
        var status = _usages.Count > 0 ? _usages[0].StatusText : "Waiting for connection";
        DrawText(g, title, titleFont, palette.Text, new Rectangle(22, 18, ClientSize.Width - 74, 24), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        DrawText(g, status, statusFont, palette.Muted, new Rectangle(22, 43, ClientSize.Width - 74, 22), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        DrawPinIcon(g, palette, GetPinBounds(), _settings.IsPinned);

        if (string.Equals(_settings.ShapeTheme, "BentoCircles", StringComparison.OrdinalIgnoreCase))
        {
            DrawBentoCircles(g, palette);
        }
        else
        {
            DrawBars(g, palette);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (GetPinBounds().Contains(e.Location))
        {
            _settings.IsPinned = !_settings.IsPinned;
            TopMost = _settings.IsPinned;
            _settings.Save();
            Invalidate();
            SettingsChanged?.Invoke();
            return;
        }

        foreach (var bounds in _timeToggleBounds)
        {
            if (bounds.Contains(e.Location))
            {
                ToggleTimeDisplayMode();
                return;
            }
        }
    }

    private void ToggleTimeDisplayMode()
    {
        _settings.TimeDisplayMode = string.Equals(_settings.TimeDisplayMode, "RemainingTime", StringComparison.OrdinalIgnoreCase)
            ? "ClockTime"
            : "RemainingTime";
        _settings.Save();
        Invalidate();
        SettingsChanged?.Invoke();
    }

    private Rectangle GetPinBounds()
    {
        return new Rectangle(ClientSize.Width - 46, 18, 24, 24);
    }

    private Rectangle GetDragHandleBounds()
    {
        return new Rectangle(10, 10, ClientSize.Width - 20, 56);
    }

    private void DrawShell(Graphics g, PopupPalette palette)
    {
        using var card = new SolidBrush(palette.Card);
        using var border = new Pen(palette.Border, 1);
        using var accent = new Pen(Color.FromArgb(118, palette.AccentBlue), 1);
        var shell = new Rectangle(7, 7, ClientSize.Width - 14, ClientSize.Height - 14);
        g.FillRoundedRectangle(card, shell, 16);
        g.DrawRoundedRectangle(border, shell, 16);
        g.DrawLine(accent, 24, 66, ClientSize.Width - 24, 66);
    }

    private void DrawPinIcon(Graphics g, PopupPalette palette, Rectangle bounds, bool pinned)
    {
        using var hoverBack = new SolidBrush(pinned ? Color.FromArgb(68, 154, 255) : Color.FromArgb(38, 42, 72));
        using var pinPen = new Pen(pinned ? Color.White : palette.Muted, 1.8f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.FillRoundedRectangle(hoverBack, bounds, 8);
        var cx = bounds.X + bounds.Width / 2;
        var top = bounds.Y + 6;
        g.DrawLine(pinPen, cx - 4, top, cx + 4, top);
        g.DrawLine(pinPen, cx - 2, top + 1, cx - 2, top + 8);
        g.DrawLine(pinPen, cx + 2, top + 1, cx + 2, top + 8);
        g.DrawLine(pinPen, cx - 6, top + 8, cx + 6, top + 8);
        g.DrawLine(pinPen, cx, top + 8, cx, top + 15);
        g.DrawLine(pinPen, cx - 3, top + 15, cx + 3, top + 15);
    }

    private void DrawBars(Graphics g, PopupPalette palette)
    {
        using var labelFont = new Font(UiFontFamily, 11f, FontStyle.Bold, GraphicsUnit.Point);
        using var metaFont = new Font(UiFontFamily, 10f, FontStyle.Regular, GraphicsUnit.Point);
        using var sectionTitleFont = new Font(UiFontFamily, 11.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var sectionStatusFont = new Font(UiFontFamily, 8.8f, FontStyle.Regular, GraphicsUnit.Point);

        var sections = BuildSections();
        var y = BarTop;
        for (var i = 0; i < sections.Count; i++)
        {
            var (usage, rows) = sections[i];
            if (i > 0)
            {
                // The first section shares the popup header; later sections get their own.
                DrawText(g, usage.DisplayName, sectionTitleFont, palette.Text, new Rectangle(22, y + 6, ClientSize.Width - 44, 22), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                DrawText(g, usage.StatusText, sectionStatusFont, palette.Muted, new Rectangle(22, y + 28, ClientSize.Width - 44, 20), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                y += SectionHeaderHeight;
            }

            foreach (var row in rows)
            {
                DrawBarRow(g, palette, new Rectangle(22, y, ClientSize.Width - 44, BarRowHeight), row, labelFont, metaFont);
                y += BarRowSpacing;
            }
        }
    }

    private void DrawBarRow(Graphics g, PopupPalette palette, Rectangle bounds, UsageRow row, Font labelFont, Font metaFont)
    {
        using var cardBrush = new SolidBrush(palette.Row);
        using var cardBorder = new Pen(palette.Border, 1);
        g.FillRoundedRectangle(cardBrush, bounds, 11);
        g.DrawRoundedRectangle(cardBorder, bounds, 11);

        var labelWidth = row.Label.Length > 6 ? 118 : 86;
        DrawText(g, row.Label, labelFont, palette.Text, new Rectangle(bounds.X + 14, bounds.Y + 10, labelWidth, 22), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        var rightX = bounds.X + labelWidth + 28;
        var rightBounds = new Rectangle(rightX, bounds.Y + 11, bounds.Right - rightX - 14, 22);

        if (row.Window is { } window)
        {
            var rightText = $"{FormatPercent(window)}  {FormatWindowTime(window)}";
            _timeToggleBounds.Add(rightBounds);
            DrawText(g, rightText, metaFont, palette.Muted, rightBounds, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            DrawBar(g, palette, new Rectangle(bounds.X + 14, bounds.Y + 36, bounds.Width - 28, 8), RoundPercent(window));
        }
        else
        {
            DrawText(g, row.DetailText ?? "--", metaFont, palette.Muted, rightBounds, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawBentoCircles(Graphics g, PopupPalette palette)
    {
        const int marginX = 22;
        const int gap = 16;
        var cardWidth = (ClientSize.Width - (marginX * 2) - gap) / 2;

        var cards = BuildSections().SelectMany(s => s.Rows).ToList();
        for (var i = 0; i < cards.Count; i++)
        {
            var col = i % 2;
            var rowIndex = i / 2;
            var bounds = new Rectangle(
                marginX + col * (cardWidth + gap),
                BentoTop + rowIndex * (BentoCardHeight + BentoRowGap),
                cardWidth,
                BentoCardHeight);
            DrawBentoCard(g, palette, bounds, cards[i]);
        }
    }

    private void DrawBentoCard(Graphics g, PopupPalette palette, Rectangle bounds, UsageRow row)
    {
        using var cardBrush = new SolidBrush(palette.Row);
        using var cardBorder = new Pen(palette.Border, 1);
        g.FillRoundedRectangle(cardBrush, bounds, 14);
        g.DrawRoundedRectangle(cardBorder, bounds, 14);

        using var labelFont = new Font(UiFontFamily, 10.2f, FontStyle.Bold, GraphicsUnit.Point);
        using var percentFont = new Font(UiFontFamily, 19f, FontStyle.Bold, GraphicsUnit.Point);
        using var metaFont = new Font(UiFontFamily, 9.2f, FontStyle.Regular, GraphicsUnit.Point);

        DrawText(g, row.Label, labelFont, palette.Text, new Rectangle(bounds.X + 16, bounds.Y + 14, bounds.Width - 32, 22), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (row.Window is { } window)
        {
            var percent = RoundPercent(window);
            var circleSize = Math.Min(126, Math.Min(bounds.Width - 54, bounds.Height - 92));
            circleSize = Math.Max(96, circleSize);
            var circle = new Rectangle(bounds.X + (bounds.Width - circleSize) / 2, bounds.Y + 50, circleSize, circleSize);
            DrawCircleGauge(g, palette, circle, percent);

            DrawText(g, $"{percent}%", percentFont, palette.Text, new Rectangle(circle.X, circle.Y + (circle.Height - 40) / 2, circle.Width, 40), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var metaY = Math.Min(bounds.Bottom - 34, circle.Bottom + 14);
            var metaBounds = new Rectangle(bounds.X + 16, metaY, bounds.Width - 32, 20);
            _timeToggleBounds.Add(metaBounds);
            DrawText(g, FormatWindowTime(window), metaFont, palette.Muted, metaBounds, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            DrawText(g, row.DetailText ?? "--", percentFont, palette.Text, new Rectangle(bounds.X + 16, bounds.Y + 50, bounds.Width - 32, bounds.Height - 100), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static void DrawBar(Graphics g, PopupPalette palette, Rectangle bounds, int percent)
    {
        using var background = new SolidBrush(palette.Track);
        using var progress = new SolidBrush(BlendAccent(palette, percent));
        g.FillRoundedRectangle(background, bounds, 5);
        var width = (int)Math.Round(bounds.Width * Math.Clamp(percent, 0, 100) / 100d);
        if (width > 0)
        {
            g.FillRoundedRectangle(progress, new Rectangle(bounds.X, bounds.Y, width, bounds.Height), 5);
        }
    }

    private static void DrawCircleGauge(Graphics g, PopupPalette palette, Rectangle bounds, int percent)
    {
        using var background = new Pen(palette.Track, 8) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var progress = new Pen(BlendAccent(palette, percent), 8) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawArc(background, bounds, -90, 360);
        g.DrawArc(progress, bounds, -90, 360 * Math.Clamp(percent, 0, 100) / 100f);
    }

    private static Color BlendAccent(PopupPalette palette, int percent)
    {
        if (percent <= 20) return Color.FromArgb(255, 126, 91);
        if (percent <= 50) return Color.FromArgb(132, 124, 255);
        return palette.AccentBlue;
    }

    private static void DrawText(Graphics g, string value, Font font, Color color, Rectangle bounds, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, value, font, bounds, color, flags | TextFormatFlags.GlyphOverhangPadding);
    }

    private static int RoundPercent(RateLimitWindow window)
    {
        return (int)Math.Round(Math.Clamp(window.RemainingPercent, 0d, 100d));
    }

    private static string FormatPercent(RateLimitWindow window)
    {
        return $"{RoundPercent(window)}%";
    }

    private string FormatWindowTime(RateLimitWindow? window)
    {
        if (window?.ResetsAt is null) return "reset --";
        if (string.Equals(_settings.TimeDisplayMode, "RemainingTime", StringComparison.OrdinalIgnoreCase))
        {
            return FormatRemaining(window.ResetsAt.Value);
        }

        var local = window.ResetsAt.Value.ToLocalTime().DateTime;
        return local.ToString("h:mm tt");
    }

    private static string FormatRemaining(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "0m";
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }
        return $"{Math.Max(1, remaining.Minutes)}m";
    }

    private static string ResolveUiFontFamily()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            foreach (var family in fonts.Families)
            {
                if (family.Name.Equals("Pretendard", StringComparison.OrdinalIgnoreCase)
                    || family.Name.Equals("Pretendard Variable", StringComparison.OrdinalIgnoreCase))
                {
                    return family.Name;
                }
            }
        }
        catch
        {
        }

        return "Segoe UI";
    }
}

internal sealed record PopupPalette(Color Background, Color Card, Color Row, Color Border, Color Track, Color Text, Color Muted, Color AccentBlue, Color AccentPurple)
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
        Color.FromArgb(11, 13, 24),
        Color.FromArgb(24, 26, 46),
        Color.FromArgb(31, 34, 60),
        Color.FromArgb(75, 82, 132),
        Color.FromArgb(58, 63, 98),
        Color.FromArgb(244, 247, 255),
        Color.FromArgb(171, 181, 214),
        Color.FromArgb(68, 154, 255),
        Color.FromArgb(156, 104, 255));

    public static PopupPalette MidnightBlack => new(
        Color.FromArgb(0, 0, 0),
        Color.FromArgb(5, 5, 7),
        Color.FromArgb(13, 13, 17),
        Color.FromArgb(46, 46, 54),
        Color.FromArgb(33, 33, 39),
        Color.FromArgb(248, 248, 250),
        Color.FromArgb(178, 180, 188),
        Color.FromArgb(75, 180, 255),
        Color.FromArgb(134, 116, 255));

    public static PopupPalette Nebula => new(
        Color.FromArgb(8, 12, 24),
        Color.FromArgb(34, 38, 66),
        Color.FromArgb(42, 48, 82),
        Color.FromArgb(112, 128, 194),
        Color.FromArgb(52, 61, 96),
        Color.FromArgb(248, 250, 255),
        Color.FromArgb(188, 199, 229),
        Color.FromArgb(74, 166, 255),
        Color.FromArgb(182, 118, 255));

    public static PopupPalette Glassmorphism => new(
        Color.FromArgb(10, 16, 28),
        Color.FromArgb(30, 46, 66),
        Color.FromArgb(39, 58, 80),
        Color.FromArgb(146, 179, 214),
        Color.FromArgb(62, 82, 108),
        Color.FromArgb(252, 254, 255),
        Color.FromArgb(206, 222, 238),
        Color.FromArgb(92, 196, 255),
        Color.FromArgb(176, 146, 255));
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
