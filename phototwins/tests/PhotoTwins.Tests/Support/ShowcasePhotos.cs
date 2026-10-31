using ImageMagick;

namespace PhotoTwins.Tests.Support;

/// <summary>
/// Made-up "holiday photos" for the showcase pictures and video clips: drawn landscapes (sky, sun, hills, water, a city at night),
/// so the app can be shown with a library that looks real. Same kind and seed = same photo, every run. Nothing here reads a real photo.
/// </summary>
public static class ShowcasePhotos
{
    public const int W = 2000, H = 1500;

    public enum Kind { SunsetLake, Beach, GreenHills, CityNight, SnowPeaks, Lavender, Dunes, Aurora, AutumnHills, MistyLake }

    private sealed record Layer(double Base, double Amp, double Rough, (double r, double g, double b) Color, double Fog);

    private sealed record Look(
        (double r, double g, double b) SkyTop, (double r, double g, double b) SkyBottom, double Horizon,
        (double x, double y, double r)? Sun, (double r, double g, double b) SunColor, double Glow,
        Layer[] Layers, bool Water = false, bool Sand = false, bool City = false, bool Stars = false,
        bool AuroraBands = false, bool Rows = false, int Clouds = 0, double Mist = 0);

    private static (double, double, double) Hex(string h) =>
        (Convert.ToInt32(h[1..3], 16), Convert.ToInt32(h[3..5], 16), Convert.ToInt32(h[5..7], 16));

    private static Look LookOf(Kind k) => k switch
    {
        Kind.SunsetLake => new(Hex("#2B1B4A"), Hex("#F5A25D"), 0.62, (0.62, 0.5, 0.045), Hex("#FFE2A8"), 0.9,
            new[] { new Layer(0.5, 0.12, 2.2, Hex("#7A4E7E"), 0.45), new Layer(0.56, 0.09, 3.1, Hex("#4B2E5C"), 0.25), new Layer(0.62, 0.05, 5, Hex("#2A1838"), 0.05) }, Water: true),
        Kind.Beach => new(Hex("#2F7FD6"), Hex("#BFE3F7"), 0.55, (0.78, 0.16, 0.035), Hex("#FFFFF2"), 0.6,
            Array.Empty<Layer>(), Water: true, Sand: true, Clouds: 4),
        Kind.GreenHills => new(Hex("#79B8EC"), Hex("#E6F3FB"), 0.9, (0.2, 0.18, 0.04), Hex("#FFFBE6"), 0.5,
            new[] { new Layer(0.48, 0.08, 1.6, Hex("#8CC46A"), 0.35), new Layer(0.6, 0.1, 1.9, Hex("#5E9E44"), 0.15), new Layer(0.76, 0.09, 2.4, Hex("#3C7A2F"), 0.0) }, Clouds: 5),
        Kind.CityNight => new(Hex("#070B22"), Hex("#2C3E7C"), 0.9, (0.18, 0.16, 0.03), Hex("#F4F1E0"), 0.25,
            Array.Empty<Layer>(), City: true, Stars: true),
        Kind.SnowPeaks => new(Hex("#3F74BE"), Hex("#D4E5F6"), 0.9, null, Hex("#FFFFFF"), 0,
            new[] { new Layer(0.36, 0.22, 2.8, Hex("#EEF3FA"), 0.1), new Layer(0.6, 0.1, 3.5, Hex("#5C7C8E"), 0.2), new Layer(0.76, 0.07, 6, Hex("#24433C"), 0.0) }, Clouds: 2),
        Kind.Lavender => new(Hex("#F3B58E"), Hex("#A9C9EA"), 0.55, (0.3, 0.42, 0.05), Hex("#FFE9C2"), 0.7,
            new[] { new Layer(0.53, 0.025, 2, Hex("#6C7A5E"), 0.4) }, Rows: true),
        Kind.Dunes => new(Hex("#F0A766"), Hex("#FBE3C0"), 0.9, (0.7, 0.24, 0.05), Hex("#FFF4DD"), 0.7,
            new[] { new Layer(0.5, 0.08, 1.2, Hex("#E3A061"), 0.3), new Layer(0.62, 0.1, 1.4, Hex("#D08445"), 0.1), new Layer(0.8, 0.08, 1.1, Hex("#B86A33"), 0.0) }),
        Kind.Aurora => new(Hex("#030716"), Hex("#0C2836"), 0.9, null, Hex("#FFFFFF"), 0,
            new[] { new Layer(0.72, 0.08, 3, Hex("#0A1218"), 0.0) }, Stars: true, AuroraBands: true),
        Kind.AutumnHills => new(Hex("#8DA9C4"), Hex("#E9E4D8"), 0.9, (0.75, 0.22, 0.04), Hex("#FFF6E0"), 0.4,
            new[] { new Layer(0.45, 0.07, 2, Hex("#C9874A"), 0.4), new Layer(0.58, 0.1, 2.6, Hex("#B4512E"), 0.2), new Layer(0.75, 0.08, 3.4, Hex("#7A3420"), 0.0) }, Clouds: 3),
        Kind.MistyLake => new(Hex("#AFC0CF"), Hex("#E8ECEF"), 0.6, (0.4, 0.34, 0.06), Hex("#FFFFFF"), 0.35,
            new[] { new Layer(0.48, 0.1, 2.4, Hex("#7E909E"), 0.55), new Layer(0.55, 0.06, 4, Hex("#4E6070"), 0.3), new Layer(0.6, 0.03, 7, Hex("#2F3F4C"), 0.1) }, Water: true, Mist: 0.35),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>One photo. <paramref name="shift"/> moves the sun and the nearest hills a little: the next shot of a burst.</summary>
    public static MagickImage Make(Kind kind, int seed, double shift = 0, int w = W, int h = H)
    {
        var look = LookOf(kind);
        var rnd = new Random(seed * 31 + (int)kind);
        // each photo of the same kind differs a little: hills, clouds and sun move with the seed
        double jitter = rnd.NextDouble() * 10;
        var ridges = look.Layers.Select((l, i) => Ridge(w, l, seed * 17 + i, jitter + (i == look.Layers.Length - 1 ? shift * 6 : 0))).ToArray();
        var clouds = Enumerable.Range(0, look.Clouds).Select(_ => (x: rnd.NextDouble(), y: 0.08 + rnd.NextDouble() * 0.3, rx: 0.08 + rnd.NextDouble() * 0.12, ry: 0.02 + rnd.NextDouble() * 0.03)).ToArray();
        var sun = look.Sun is { } s ? (x: s.x + (rnd.NextDouble() - 0.5) * 0.1 + shift, y: s.y, r: s.r) : default;
        var city = look.City ? Buildings(rnd) : null;
        double aspect = (double)w / h;
        var px = new byte[w * h * 3];

        (double r, double g, double b) Above(double fx, double fy)
        {
            double t = Math.Clamp(fy / look.Horizon, 0, 1);
            t = t * t * (3 - 2 * t);
            double r = look.SkyTop.r + (look.SkyBottom.r - look.SkyTop.r) * t;
            double g = look.SkyTop.g + (look.SkyBottom.g - look.SkyTop.g) * t;
            double b = look.SkyTop.b + (look.SkyBottom.b - look.SkyTop.b) * t;
            if (look.Stars && fy < 0.65)
            {
                uint hsh = Hash((int)(fx * 1000), (int)(fy * 750), seed);
                if (hsh % 1000 < 2) { double v = 120 + hsh % 135; r = Math.Max(r, v); g = Math.Max(g, v); b = Math.Max(b, v); }
            }
            if (look.AuroraBands)
            {
                for (int band = 0; band < 2; band++)
                {
                    double cy = 0.22 + band * 0.12 + 0.07 * Math.Sin(fx * (5 + band) + seed + band * 2 + shift * 3);
                    double a = Math.Exp(-Math.Pow((fy - cy) / 0.045, 2)) * (0.55 + 0.45 * Math.Sin(fx * 23 + band * 4 + seed));
                    a += Math.Exp(-Math.Pow((fy - cy + 0.08) / 0.12, 2)) * 0.25 * a;
                    r += a * 40; g += a * 210; b += a * (band == 0 ? 120 : 170);
                }
            }
            if (look.Sun != null)
            {
                double dx = (fx - sun.x) * aspect, dy = fy - sun.y, d = Math.Sqrt(dx * dx + dy * dy);
                double glow = Math.Exp(-d / (sun.r * 4)) * look.Glow;
                r += (look.SunColor.r - r) * glow * 0.8; g += (look.SunColor.g - g) * glow * 0.8; b += (look.SunColor.b - b) * glow * 0.8;
                if (d < sun.r) { double e = Math.Clamp((sun.r - d) / (sun.r * 0.08), 0, 1); r += (look.SunColor.r - r) * e; g += (look.SunColor.g - g) * e; b += (look.SunColor.b - b) * e; }
            }
            foreach (var c in clouds)
            {
                double dx = (fx - c.x) / c.rx, dy = (fy - c.y) / c.ry, q = dx * dx + dy * dy;
                double puff = 0.5 + 0.5 * Math.Sin(fx * 60 + c.y * 40);
                double a = Math.Clamp(1.2 - q * (0.8 + 0.4 * puff), 0, 1) * 0.85;
                r += (250 - r) * a; g += (250 - g) * a; b += (252 - b) * a;
            }
            int col = Math.Clamp((int)(fx * w), 0, w - 1);
            for (int i = 0; i < ridges.Length; i++)
            {
                if (fy < ridges[i][col]) continue;
                var l = look.Layers[i];
                double depth = Math.Clamp((fy - ridges[i][col]) * 2.5, 0, 1);
                double lr = l.Color.r * (1 - 0.18 * depth), lg = l.Color.g * (1 - 0.18 * depth), lb = l.Color.b * (1 - 0.18 * depth);
                double tex = (int)(Hash(col / 3, (int)(fy * h) / 3, seed + i) % 17) - 8;
                r = lr + (look.SkyBottom.r - lr) * l.Fog + tex; g = lg + (look.SkyBottom.g - lg) * l.Fog + tex; b = lb + (look.SkyBottom.b - lb) * l.Fog + tex;
            }
            if (city != null)
            {
                foreach (var bd in city)
                {
                    if (fx < bd.x0 || fx > bd.x1 || fy < bd.top) continue;
                    r = 14; g = 18; b = 38;
                    int wx = (int)((fx - bd.x0) * w / 14), wy = (int)((fy - bd.top) * h / 18);
                    bool inWin = ((fx - bd.x0) * w % 14) > 5 && ((fy - bd.top) * h % 18) > 7;
                    if (inWin && Hash(wx, wy, bd.seed) % 100 < 38) { r = 255; g = 205; b = 120; }
                }
            }
            if (look.Rows && fy > look.Horizon)
            {
                double depth = (fy - look.Horizon) / (1 - look.Horizon);
                double u = (fx - 0.5) / (depth + 0.04);
                double stripe = 0.5 + 0.5 * Math.Sin(u * 26);
                double pr = 120 + 40 * depth, pg = 86 + 20 * depth, pb = 170 + 30 * depth;
                double gr = 60, gg = 82, gb = 50;
                r = gr + (pr - gr) * stripe; g = gg + (pg - gg) * stripe; b = gb + (pb - gb) * stripe;
            }
            if (look.Mist > 0)
            {
                double m = Math.Exp(-Math.Pow((fy - look.Horizon + 0.02) / 0.08, 2)) * look.Mist;
                r += (240 - r) * m; g += (243 - g) * m; b += (246 - b) * m;
            }
            return (r, g, b);
        }

        Parallel.For(0, h, y =>
        {
            double fy = (double)y / h;
            var noise = new Random(seed * 7919 + y);
            for (int x = 0; x < w; x++)
            {
                double fx = (double)x / w;
                (double r, double g, double b) c;
                if (look.Water && fy > look.Horizon && !(look.Sand && fy > SandLine(fx)))
                {
                    double depth = fy - look.Horizon;
                    double ripple = Math.Sin(fy * 900 + Math.Sin(fx * 40) * 2) * 0.004 * (1 + depth * 6);
                    var m = Above(fx + ripple, look.Horizon - depth * 0.9);
                    double dark = 0.72 - depth * 0.3;
                    c = (m.r * dark + 20 * (1 - dark), m.g * dark + 50 * (1 - dark), m.b * dark + 80 * (1 - dark));
                    if (look.Sand && fy > SandLine(fx) - 0.012) c = (235, 240, 242);   // the foam where the sea meets the sand
                }
                else if (look.Sand && fy > SandLine(fx))
                {
                    double t = (fy - SandLine(fx)) * 3;
                    c = (233 - 20 * t, 212 - 26 * t, 164 - 30 * t);
                }
                else c = Above(fx, fy);
                int n = noise.Next(-4, 5);
                int i = (y * w + x) * 3;
                px[i] = Clamp(c.r + n); px[i + 1] = Clamp(c.g + n); px[i + 2] = Clamp(c.b + n);
            }
        });
        return new MagickImage(px, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
    }

    private static double SandLine(double fx) => 0.78 + 0.025 * Math.Sin(fx * 9 + 1.3);

    private static double[] Ridge(int w, Layer l, int seed, double phase)
    {
        var y = new double[w];
        for (int x = 0; x < w; x++)
        {
            double fx = (double)x / w * l.Rough + phase, v = 0, amp = 1, freq = 1, norm = 0;
            for (int o = 0; o < 5; o++) { v += Noise1(fx * freq, seed + o) * amp; norm += amp; amp *= 0.5; freq *= 2.1; }
            y[x] = l.Base - l.Amp * 2.2 * (v / norm - 0.3);   // stretched, so the hills rise and fall like real ones
        }
        return y;
    }

    private static (double x0, double x1, double top, int seed)[] Buildings(Random rnd)
    {
        var list = new List<(double, double, double, int)>();
        double x = -0.02;
        while (x < 1)
        {
            double width = 0.04 + rnd.NextDouble() * 0.07;
            list.Add((x, x + width, 0.42 + rnd.NextDouble() * 0.38, rnd.Next()));
            x += width + 0.004;
        }
        return list.ToArray();
    }

    private static double Noise1(double x, int seed)
    {
        int i = (int)Math.Floor(x);
        double f = x - i, t = f * f * (3 - 2 * f);
        double a = Hash(i, 0, seed) / (double)uint.MaxValue, b = Hash(i + 1, 0, seed) / (double)uint.MaxValue;
        return a + (b - a) * t;
    }

    private static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }
    }

    private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, v));
}
