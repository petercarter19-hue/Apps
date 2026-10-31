using ImageMagick;
using ImageMagick.Formats;

namespace PhotoTwins.Tests.Support;

/// <summary>What a camera would write into a photo's hidden info.</summary>
public sealed record Meta(
    string? Taken = null,      // "2026:10:12 14:03:09"
    string? SubSec = null,     // "07" or "412"
    string? Make = null,
    string? Model = null,
    string? Serial = null,
    string? UniqueId = null,
    bool Gps = false,
    ushort? Orientation = null);

/// <summary>
/// Makes made-up "photos": a seeded scene (sky gradient, soft clouds, a few shapes, a little noise) and every kind of copy
/// of it the user's computer could hold: resized, re-saved, other type, chat-app version, rotated, burst neighbour, friend's shot.
/// Same seed = same scene, every run. Nothing here reads any real photo.
/// </summary>
public static class TestScenes
{
    public const int W = 640, H = 480;

    /// <summary>The scene. <paramref name="subjectShiftPct"/> moves the two "subject" shapes sideways (a burst neighbour).</summary>
    public static MagickImage Scene(int seed, double subjectShiftPct = 0, int noiseSeed = 0, int w = W, int h = H)
    {
        var rnd = new Random(seed);
        double[] top = { rnd.Next(40, 200), rnd.Next(80, 220), rnd.Next(120, 255) };
        double[] bottom = { rnd.Next(30, 160), rnd.Next(60, 200), rnd.Next(30, 160) };
        var waves = Enumerable.Range(0, 4).Select(_ => (fx: rnd.NextDouble() * 6 + 1, fy: rnd.NextDouble() * 6 + 1, ph: rnd.NextDouble() * 6.28, amp: rnd.Next(14, 40))).ToArray();
        var shapes = Enumerable.Range(0, 8).Select(i => (
            ellipse: rnd.Next(2) == 0,
            cx: 0.12 + rnd.NextDouble() * 0.76, cy: 0.12 + rnd.NextDouble() * 0.76,
            rx: 0.05 + rnd.NextDouble() * 0.14, ry: 0.05 + rnd.NextDouble() * 0.14,
            r: rnd.Next(10, 245), g: rnd.Next(10, 245), b: rnd.Next(10, 245))).ToArray();
        var noise = new Random(noiseSeed * 7919 + 17);
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            double fy = (double)y / h;
            for (int x = 0; x < w; x++)
            {
                double fx = (double)x / w;
                double r = top[0] + (bottom[0] - top[0]) * fy, g = top[1] + (bottom[1] - top[1]) * fy, b = top[2] + (bottom[2] - top[2]) * fy;
                foreach (var wv in waves)
                {
                    double v = Math.Sin(fx * wv.fx * 3.14 + fy * wv.fy * 3.14 + wv.ph) * wv.amp;
                    r += v; g += v * 0.8; b += v * 0.6;
                }
                for (int s = 0; s < shapes.Length; s++)
                {
                    var sh = shapes[s];
                    double cx = sh.cx + (s < 2 ? subjectShiftPct / 100.0 : 0);
                    double dx = (fx - cx) / sh.rx, dy = (fy - sh.cy) / sh.ry;
                    bool inside = sh.ellipse ? dx * dx + dy * dy <= 1 : Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
                    if (inside) { double shade = sh.ellipse ? 1 - 0.3 * (dx * dx + dy * dy) : 1; r = sh.r * shade; g = sh.g * shade; b = sh.b * shade; }
                }
                int n = noise.Next(-5, 6);
                int i = (y * w + x) * 3;
                px[i] = Clamp(r + n); px[i + 1] = Clamp(g + n); px[i + 2] = Clamp(b + n);
            }
        }
        return new MagickImage(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
    }

    private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, v));

    private static ExifProfile? Exif(Meta? m)
    {
        if (m == null) return null;
        var e = new ExifProfile();
        if (m.Taken != null) e.SetValue(ExifTag.DateTimeOriginal, m.Taken);
        if (m.SubSec != null) e.SetValue(ExifTag.SubsecTimeOriginal, m.SubSec);
        if (m.Make != null) e.SetValue(ExifTag.Make, m.Make);
        if (m.Model != null) e.SetValue(ExifTag.Model, m.Model);
        if (m.Serial != null) e.SetValue(ExifTag.SerialNumber, m.Serial);
        if (m.UniqueId != null) e.SetValue(ExifTag.ImageUniqueID, m.UniqueId);
        if (m.Orientation != null) e.SetValue(ExifTag.Orientation, m.Orientation.Value);
        if (m.Gps)
        {
            e.SetValue(ExifTag.GPSLatitudeRef, "N");
            e.SetValue(ExifTag.GPSLatitude, new[] { new Rational(34, 1), new Rational(44, 1), new Rational(10, 1) });
            e.SetValue(ExifTag.GPSLongitudeRef, "W");
            e.SetValue(ExifTag.GPSLongitude, new[] { new Rational(86, 1), new Rational(35, 1), new Rational(5, 1) });
        }
        return e;
    }

    /// <summary>Saves an image as the given type, optionally resized (percent) and with camera info written in.</summary>
    public static byte[] Encode(MagickImage source, MagickFormat format, int quality = 92, Meta? meta = null, int? percent = null)
    {
        using var img = (MagickImage)source.Clone();
        if (percent != null) img.Resize(new Percentage(percent.Value));
        var exif = Exif(meta);
        if (exif != null) img.SetProfile(exif);
        img.Quality = (uint)quality;
        img.Format = format;
        return img.ToByteArray();
    }

    /// <summary>The same picture as a phone would store it rotated: pixels turned the other way plus an orientation note.</summary>
    public static byte[] EncodeRotatedWithOrientationNote(MagickImage source, Meta? meta = null)
    {
        using var img = (MagickImage)source.Clone();
        img.Rotate(-90);   // pixels turned counter-clockwise ...
        img.Orientation = Orientation.RightTop;
        var m = (meta ?? new Meta()) with { Orientation = 6 };   // ... and "display rotated 90 degrees clockwise"
        img.SetProfile(Exif(m)!);
        img.Format = MagickFormat.Jpeg; img.Quality = 92;
        return img.ToByteArray();
    }

    /// <summary>What a chat app sends back: smaller, re-compressed, all hidden info removed.</summary>
    public static byte[] ChatAppVersion(MagickImage source)
    {
        using var img = (MagickImage)source.Clone();
        img.Resize(new Percentage(60));
        img.Strip();
        img.Quality = 70; img.Format = MagickFormat.Jpeg;
        return img.ToByteArray();
    }

    public static bool CanWrite(MagickFormat f)
    {
        try { using var i = Scene(1, w: 64, h: 48); i.Format = f; return i.ToByteArray().Length > 0; }
        catch { return false; }
    }
}
