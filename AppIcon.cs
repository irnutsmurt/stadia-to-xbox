using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace StadiaXbox;

public enum BridgeStatus { Stopped, Searching, Connected }

/// <summary>
/// The app icon, drawn in code so there is no .ico to ship: a Stadia-coloured tile
/// with a controller glyph. The tray version adds a status dot (green = controller
/// bridged, amber = searching) and turns grey while the bridge is stopped.
/// </summary>
internal static class AppIcon
{
    private static readonly Color StadiaOrange = Color.FromArgb(255, 76, 29);
    private static readonly Color StadiaPurple = Color.FromArgb(155, 0, 99);
    private static readonly Color Connected = Color.FromArgb(100, 210, 130);
    private static readonly Color Searching = Color.FromArgb(230, 190, 90);

    public static Icon Create(int size, BridgeStatus? status = null)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            bool stopped = status == BridgeStatus.Stopped;
            var tile = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using (var path = RoundedRect(tile, size * 0.22f))
            using (var fill = new LinearGradientBrush(tile,
                       stopped ? Color.FromArgb(125, 125, 132) : StadiaOrange,
                       stopped ? Color.FromArgb(70, 70, 78) : StadiaPurple,
                       LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(fill, path);
            }

            DrawGlyph(g, size);

            if (status is BridgeStatus.Connected or BridgeStatus.Searching)
            {
                float r = size * 0.2f;
                var dot = new RectangleF(size - 2 * r - 0.5f, size - 2 * r - 0.5f, 2 * r, 2 * r);
                using var brush = new SolidBrush(status == BridgeStatus.Connected ? Connected : Searching);
                using var ring = new Pen(Color.FromArgb(24, 25, 29), Math.Max(1f, size / 16f));
                g.FillEllipse(brush, dot);
                g.DrawEllipse(ring, dot);
            }
        }

        // GetHicon's handle isn't owned by the Icon wrapper; Clone makes an owned copy.
        IntPtr h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    // The "Game" controller glyph from the Windows icon font, scaled to fit the tile.
    private static void DrawGlyph(Graphics g, int size)
    {
        using var family = IconFont();
        if (family is null) return;

        using var glyph = new GraphicsPath();
        glyph.AddString("", family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var b = glyph.GetBounds();
        if (b.Width <= 0) return;

        float scale = size * 0.72f / b.Width;
        using var m = new Matrix();
        m.Translate(size / 2f, size / 2f);
        m.Scale(scale, scale);
        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
        glyph.Transform(m);
        g.FillPath(Brushes.White, glyph);
    }

    private static FontFamily? IconFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
