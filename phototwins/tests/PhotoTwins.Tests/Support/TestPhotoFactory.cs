using ImageMagick;

namespace PhotoTwins.Tests.Support;

/// <summary>
/// Makes small, repeatable test photos for the scan tests (same seed = same picture; different seeds = clearly different pictures).
/// The matching tests use the bigger scenes in <see cref="TestScenes"/>. Never reads anything from anywhere.
/// </summary>
public static class TestPhotoFactory
{
    private const int SmallW = 160, SmallH = 120;

    private static byte[] Make(int seed, MagickFormat f, int quality = 90)
    {
        using var scene = TestScenes.Scene(seed, w: SmallW, h: SmallH);
        return TestScenes.Encode(scene, f, quality);
    }

    public static byte[] Jpeg(int seed, int w = 0, int h = 0, int quality = 90) => Make(seed, MagickFormat.Jpeg, quality);
    public static byte[] Png(int seed, int w = 0, int h = 0) => Make(seed, MagickFormat.Png);
    public static byte[] Bmp(int seed, int w = 0, int h = 0) => Make(seed, MagickFormat.Bmp);
    public static byte[] Tiff(int seed, int w = 0, int h = 0) => Make(seed, MagickFormat.Tiff);

    /// <summary>
    /// A real, openable photo of the given size in bytes (a small JPEG followed by padding, which every reader ignores).
    /// Different seeds give different pictures. Lets scan tests have "3.4 MB photos" without making huge pictures.
    /// </summary>
    public static byte[] StandIn(int seed, int length = 4000)
    {
        var jpeg = Jpeg(seed);
        if (length <= jpeg.Length) return jpeg;
        var b = new byte[length];
        Array.Copy(jpeg, b, jpeg.Length);
        new Random(seed).NextBytes(b.AsSpan(jpeg.Length));
        return b;
    }

    public static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
