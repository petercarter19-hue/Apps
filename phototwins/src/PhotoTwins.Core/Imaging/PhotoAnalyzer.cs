using ImageMagick;

namespace PhotoTwins.Core.Imaging;

/// <summary>What reading one photo gave: its fingerprint (null if the picture couldn't be opened), its hidden info, and why not.</summary>
/// <param name="Thumb">The small picture for the cards, made while the photo was open. Never saved with the rest: the scan hands it to the picture memory and drops it.</param>
public sealed record Analysis(Fingerprint? Fingerprint, PhotoInfo Info, string? Error, byte[]? Thumb = null)
{
    public static Analysis Failed(string error) => new(null, PhotoInfo.None, error);
}

/// <summary>
/// Opens a photo's bytes with the free built-in image reader (JPG, PNG, HEIC, WEBP, BMP, TIFF) and makes its fingerprint.
/// Only ever given bytes the app has already read from a file: never a path or a web address.
/// </summary>
public sealed class PhotoAnalyzer
{
    /// <summary>Photos bigger than this aren't opened as pictures (they are still compared as exact copies).</summary>
    public const long MaxBytes = 400L * 1024 * 1024;

    static PhotoAnalyzer() => Init();

    /// <summary>Sets the image reader's limits once, whichever part of the app uses it first.</summary>
    internal static void Init()
    {
        ResourceLimits.Thread = 1;                     // we already read many photos at once
        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
        ResourceLimits.Area = 1_500_000_000UL;         // refuse absurdly large pictures instead of running out of memory
        ResourceLimits.Memory = 3UL * 1024 * 1024 * 1024;
    }

    private static readonly System.Text.RegularExpressions.Regex DamagedWarning = new(
        "premature end|truncat|not enough image data|insufficient image data|unexpected end of file",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public Analysis Analyze(byte[] bytes)
    {
        var info = PhotoInfoReader.Read(bytes);
        try
        {
            var size = new MagickImageInfo(bytes);
            var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
            // Big JPEGs decode at a fraction of their size (far faster). Small ones must not: the decoder would scale them UP.
            if (Math.Max(size.Width, size.Height) > 1536) settings.SetDefine(MagickFormat.Jpeg, "size", "512x512");
            using var img = new MagickImage();
            var warnings = new List<string>();
            img.Warning += (_, e) => warnings.Add(e.Message);
            img.Read(bytes, settings);
            // The reader quietly patches up cut-off files. A photo that ended early is damaged: say so instead of comparing half a picture.
            var cutOff = warnings.FirstOrDefault(w => DamagedWarning.IsMatch(w));
            if (cutOff != null) return new Analysis(null, info, "It looks damaged: the file ends before the picture does.");

            int w = (int)size.Width, h = (int)size.Height;
            if (img.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom)
                (w, h) = (h, w);   // shown turned on its side: its true shape is the other way round
            img.AutoOrient();
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaAction.Remove);
            byte[]? thumb = null;
            try { thumb = PhotoPictures.ThumbJpeg(img); } catch { /* the card's picture is simply made later instead */ }
            img.Grayscale();
            img.FilterType = FilterType.Box;

            var g32 = Gray(img, 32, 32);
            var g9 = Gray(img, 9, 8);
            return new Analysis(new Fingerprint(FingerprintMath.PHash(g32), FingerprintMath.DHash(g9), FingerprintMath.Tiny(g32), w, h), info, null, thumb);
        }
        catch (Exception ex)
        {
            return new Analysis(null, info, Friendly(ex));
        }
    }

    private static byte[] Gray(MagickImage source, uint w, uint h)
    {
        using var small = (MagickImage)source.Clone();
        small.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("The picture had no pixels.");
        var g = new byte[w * h];
        for (int i = 0; i < g.Length; i++) g[i] = rgb[i * 3];
        return g;
    }

    public static string Friendly(Exception ex)
    {
        var msg = ex.Message.Split('\n')[0].Trim();
        // The reader's own messages can include internal file names: keep only the short plain part.
        int at = msg.IndexOf(" @ ", StringComparison.Ordinal);
        if (at > 0) msg = msg[..at];
        if (msg.Length > 140) msg = msg[..140] + "…";
        return $"It couldn't be opened as a picture ({msg}).";
    }
}
