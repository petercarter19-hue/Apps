using System.Numerics;

namespace PhotoTwins.Core.Imaging;

/// <summary>
/// A photo's "fingerprint": small numbers that stay nearly the same when the picture is resized, re-saved or changed to another type.
/// </summary>
/// <param name="PHash">64 bits from the picture's broad light and dark pattern (a discrete cosine transform of a 32 x 32 grayscale copy).</param>
/// <param name="DHash">64 bits from which neighbouring spots of a 9 x 8 grayscale copy are brighter.</param>
/// <param name="Tiny">A 16 x 16 grayscale copy (256 bytes), used to confirm a match and to tell burst shots apart.</param>
/// <param name="Width">True width in pixels, as the photo is shown (after rotation).</param>
/// <param name="Height">True height in pixels, as the photo is shown.</param>
public sealed record Fingerprint(ulong PHash, ulong DHash, byte[] Tiny, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
    public double LogAspect => Math.Log((double)Width / Height);
}

public static class FingerprintMath
{
    private static readonly double[,] Cos = BuildCos();

    private static double[,] BuildCos()
    {
        var c = new double[8, 32];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
        return c;
    }

    /// <summary>The 64-bit picture-pattern hash of a 32 x 32 grayscale picture (1,024 bytes, row by row).</summary>
    public static ulong PHash(ReadOnlySpan<byte> gray32)
    {
        if (gray32.Length != 32 * 32) throw new ArgumentException("Expected a 32 x 32 picture.");
        var rows = new double[32 * 8];
        for (int y = 0; y < 32; y++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int x = 0; x < 32; x++) s += gray32[y * 32 + x] * Cos[u, x];
                rows[y * 8 + u] = s;
            }
        var coef = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int y = 0; y < 32; y++) s += rows[y * 8 + u] * Cos[v, y];
                coef[v * 8 + u] = s;
            }
        var ac = coef.Skip(1).OrderBy(x => x).ToArray();   // 63 values without the overall-brightness term
        double median = ac[31];
        ulong bits = 0;
        for (int i = 1; i < 64; i++)
            if (coef[i] > median) bits |= 1UL << i;
        return bits;
    }

    /// <summary>The 64-bit "which side is brighter" hash of a 9 x 8 grayscale picture (72 bytes, row by row).</summary>
    public static ulong DHash(ReadOnlySpan<byte> gray9x8)
    {
        if (gray9x8.Length != 72) throw new ArgumentException("Expected a 9 x 8 picture.");
        ulong bits = 0;
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (gray9x8[r * 9 + c] < gray9x8[r * 9 + c + 1]) bits |= 1UL << (r * 8 + c);
        return bits;
    }

    /// <summary>16 x 16 copy made by averaging each 2 x 2 block of the 32 x 32 picture.</summary>
    public static byte[] Tiny(ReadOnlySpan<byte> gray32)
    {
        var t = new byte[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int s = gray32[(2 * y) * 32 + 2 * x] + gray32[(2 * y) * 32 + 2 * x + 1] + gray32[(2 * y + 1) * 32 + 2 * x] + gray32[(2 * y + 1) * 32 + 2 * x + 1];
                t[y * 16 + x] = (byte)((s + 2) / 4);
            }
        return t;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Average difference between two tiny copies after evening out their overall brightness (0 to 255 scale).</summary>
    public static double TinyDifference(byte[] a, byte[] b)
    {
        double ma = Mean(a), mb = Mean(b), s = 0;
        for (int i = 0; i < 256; i++) s += Math.Abs((a[i] - ma) - (b[i] - mb));
        return s / 256;
    }

    public static double Mean(byte[] t)
    {
        double s = 0;
        foreach (var b in t) s += b;
        return s / t.Length;
    }

    /// <summary>How much light and dark variety the tiny copy has. A blank or one-colour picture has none.</summary>
    public static double Spread(byte[] t)
    {
        double m = Mean(t), s = 0;
        foreach (var b in t) s += (b - m) * (b - m);
        return Math.Sqrt(s / t.Length);
    }
}
