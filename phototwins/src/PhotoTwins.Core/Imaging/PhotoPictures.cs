using ImageMagick;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Imaging;

/// <summary>Reads a photo for showing on screen (small pictures and the full-size viewer). Read-only, through the same file system the scan uses.</summary>
public static class PhotoPictures
{
    static PhotoPictures() => PhotoAnalyzer.Init();

    /// <summary>The whole photo file as bytes. Refuses online-only photos (so nothing is ever downloaded) and anything over the size limit.</summary>
    public static byte[] ReadBytes(IFileSystem fs, string path, long maxBytes = PhotoAnalyzer.MaxBytes)
    {
        if (CloudAttributes.IsOnlineOnly(fs.GetAttributes(path)))
            throw new IOException("It is online-only (not stored on this PC), so PhotoTwins won't open it.");
        using var open = fs.OpenRead(path);
        if (CloudAttributes.IsOnlineOnly(open.Info.Attributes))
            throw new IOException("It is online-only (not stored on this PC), so PhotoTwins won't open it.");
        long size = open.Info.Size;
        if (size > maxBytes) throw new IOException("It is too large to show here.");
        var bytes = new byte[size];
        open.Stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A plain-English reason a photo couldn't be shown (it was moved, can't be opened, isn't a picture...).</summary>
    public static string WhyNotShown(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "The photo was moved or deleted after the scan. Scan again to refresh the list.",
        UnauthorizedAccessException => "Windows wouldn't let PhotoTwins open it (no permission).",
        IOException io when io.GetType() == typeof(IOException) && (io.HResult & 0xFFFF) is 32 or 33 => "Another program is using it right now.",
        IOException io => io.Message,   // PhotoTwins' own messages (online-only, too large)
        _ => PhotoAnalyzer.Friendly(ex),
    };

    /// <summary>How big (pixels along the long side) the small pictures on the cards are. Enough for a card at 150% screen scale.</summary>
    public const int ThumbEdge = 448;
    private const uint ThumbQuality = 74;

    /// <summary>A photo that is already open as a picture (turned the right way up, transparent parts on white) as a small JPEG for the cards.</summary>
    public static byte[] ThumbJpeg(MagickImage ready)
    {
        using var t = (MagickImage)ready.Clone();
        if (t.Width > ThumbEdge || t.Height > ThumbEdge)
            t.Thumbnail(new MagickGeometry((uint)ThumbEdge, (uint)ThumbEdge));   // fast, keeps the shape, never enlarges
        t.Strip();
        t.Quality = ThumbQuality;
        t.Format = MagickFormat.Jpeg;
        return t.ToByteArray();
    }

    /// <summary>Makes the small JPEG for a card from a photo's bytes (used when the scan didn't make one).</summary>
    public static byte[] MakeThumb(byte[] fileBytes)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        var size = new MagickImageInfo(fileBytes);
        // A JPEG much bigger than we need is decoded at a fraction of its size (several times faster).
        if (Math.Max(size.Width, size.Height) > 2 * (uint)ThumbEdge) settings.SetDefine(MagickFormat.Jpeg, "size", $"{ThumbEdge}x{ThumbEdge}");
        using var img = new MagickImage(fileBytes, settings);
        img.AutoOrient();
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
        return ThumbJpeg(img);
    }

    /// <summary>The picture the way it should be shown (turned the right way up, transparent parts on white), shrunk to fit <paramref name="maxEdge"/> pixels, as a BMP.</summary>
    public static byte[] MakeBmp(byte[] fileBytes, int maxEdge, out int width, out int height)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        var size = new MagickImageInfo(fileBytes);
        // A JPEG much bigger than we need is decoded at a fraction of its size (faster). Smaller ones must not be: the decoder would scale them up.
        if (Math.Max(size.Width, size.Height) > 2 * (uint)maxEdge) settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxEdge}x{maxEdge}");
        using var img = new MagickImage(fileBytes, settings);
        img.AutoOrient();
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
        if (img.Width > maxEdge || img.Height > maxEdge)
            img.Resize(new MagickGeometry((uint)maxEdge, (uint)maxEdge));   // keeps the shape, never enlarges
        width = (int)img.Width;
        height = (int)img.Height;
        img.Format = MagickFormat.Bmp3;
        return img.ToByteArray();
    }
}
