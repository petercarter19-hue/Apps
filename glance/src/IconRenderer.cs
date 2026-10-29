using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Glance;

/// <summary>Draws the little percentage tile that lives in the tray.</summary>
internal sealed class IconRenderer
{
    private readonly int _size;

    public IconRenderer(int size) => _size = size;

    public int Size => _size;

    /// <summary>Returns an HICON the caller owns (hand it to TrayIcon.SetIcon, which destroys it later).</summary>
    public IntPtr Render(int percent, bool pulse)
    {
        using var bmp = RenderBitmap(percent, pulse, _size);
        return bmp.GetHicon();
    }

    public static Bitmap RenderBitmap(int percent, bool pulse, int size)
    {
        percent = Math.Clamp(percent, 0, 100);
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var (fill, text) = TileColors(percent, pulse);
        using (var path = Rounded(new RectangleF(0, 0, size, size), size * 0.25f))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        string label = percent >= 100 ? "99" : percent.ToString();
        float px = size * (label.Length == 1 ? 0.74f : 0.66f);
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(text);
        g.DrawString(label, font, tb, new RectangleF(0, size * 0.03f, size, size), sf);
        return bmp;
    }

    public static (Color fill, Color text) TileColors(int p, bool pulse)
    {
        if (pulse) return (Color.FromArgb(226, 75, 74), Color.White);
        if (p >= 85) return (Color.FromArgb(240, 149, 149), Color.FromArgb(80, 19, 19));
        if (p >= 60) return (Color.FromArgb(250, 199, 117), Color.FromArgb(65, 36, 2));
        return (Color.FromArgb(192, 221, 151), Color.FromArgb(23, 52, 4));
    }

    public static Color BarColor(double p)
    {
        if (p >= 85) return Color.FromArgb(226, 75, 74);
        if (p >= 60) return Color.FromArgb(239, 159, 39);
        return Color.FromArgb(99, 153, 34);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
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
}
