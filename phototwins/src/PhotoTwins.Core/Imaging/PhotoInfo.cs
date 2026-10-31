using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;

namespace PhotoTwins.Core.Imaging;

/// <summary>
/// The hidden info a camera writes into a photo: when it was taken and with what. Anything missing is simply null:
/// chat apps strip it, and a missing piece is never treated as a disagreement.
/// </summary>
public sealed record PhotoInfo(
    long? TakenTicks,      // date and time taken as written (no time zone): ticks of a plain DateTime
    string? SubSec,        // fractions of a second, digits only ("07", "412")
    string? Make,
    string? Model,
    string? Serial,
    string? UniqueId,
    bool HasGps)
{
    public static readonly PhotoInfo None = new(null, null, null, null, null, null, false);

    public DateTime? Taken => TakenTicks is { } t ? new DateTime(t, DateTimeKind.Unspecified) : null;
    public bool IsEmpty => this == None || (TakenTicks == null && Make == null && Model == null && Serial == null && UniqueId == null && !HasGps);
}

public static class PhotoInfoReader
{
    /// <summary>Reads the hidden info from the photo's bytes (never from the picture itself). Returns None if there is none or it can't be read.</summary>
    public static PhotoInfo Read(byte[] bytes)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(new MemoryStream(bytes, writable: false));
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();

            DateTime? taken = null;
            if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) && dt.Year >= 1990) taken = dt;
            string? subsec = Digits(sub?.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal));
            if (taken == null) (taken, subsec) = ReadXmpDate(dirs);

            return new PhotoInfo(
                taken?.Ticks,
                taken == null ? null : subsec,
                Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
                Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
                CleanId(sub?.GetString(ExifDirectoryBase.TagBodySerialNumber)),
                CleanId(sub?.GetString(ExifDirectoryBase.TagImageUniqueId)),
                gps != null && gps.GetGeoLocation() != null);
        }
        catch
        {
            return PhotoInfo.None;   // no readable hidden info: fine, the picture itself is judged separately
        }
    }

    private static (DateTime? date, string? fraction) ReadXmpDate(IEnumerable<MetadataExtractor.Directory> dirs)
    {
        foreach (var xmp in dirs.OfType<XmpDirectory>())
        {
            var props = xmp.GetXmpProperties();
            foreach (var key in new[] { "exif:DateTimeOriginal", "photoshop:DateCreated", "xmp:CreateDate" })
                if (props.TryGetValue(key, out var v) && DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) && d.Year >= 1990)
                {
                    var fraction = System.Text.RegularExpressions.Regex.Match(v, @"\d{2}:\d{2}:\d{2}\.(\d+)");
                    return (d.DateTime, fraction.Success ? fraction.Groups[1].Value : null);   // as written, without converting time zones
                }
        }
        return (null, null);
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Trim('\0');
        return s.Length == 0 ? null : s;
    }

    private static string? CleanId(string? s)
    {
        s = Clean(s);
        return s == null || s.All(c => c == '0') ? null : s;
    }

    private static string? Digits(string? s)
    {
        if (s == null) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }
}

/// <summary>The "photo info doesn't disagree" part of the same-photo rule.</summary>
public static class InfoRules
{
    /// <summary>
    /// True only when something both photos have is different: the date and time taken (to the second, and to the fraction
    /// of a second when both have it), the camera, or the camera's serial or photo ID. A missing piece never disagrees.
    /// </summary>
    public static bool Disagree(PhotoInfo a, PhotoInfo b)
    {
        if (a.TakenTicks is { } ta && b.TakenTicks is { } tb)
        {
            if (ta / TimeSpan.TicksPerSecond != tb / TimeSpan.TicksPerSecond) return true;
            if (a.SubSec != null && b.SubSec != null)
            {
                int n = Math.Min(a.SubSec.Length, b.SubSec.Length);
                if (!string.Equals(a.SubSec[..n], b.SubSec[..n], StringComparison.Ordinal)) return true;
            }
        }
        if (a.Make != null && b.Make != null && FirstWord(a.Make) != FirstWord(b.Make)) return true;
        if (a.Model != null && b.Model != null && NormModel(a.Model, a.Make) != NormModel(b.Model, b.Make)) return true;
        if (a.Serial != null && b.Serial != null && !string.Equals(a.Serial, b.Serial, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.UniqueId != null && b.UniqueId != null && !string.Equals(a.UniqueId, b.UniqueId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Squash(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FirstWord(string make) => Squash(make).Split(' ')[0];

    /// <summary>"Apple iPhone 15" and "iPhone 15" are the same camera: the maker's name repeated inside the model is dropped.</summary>
    private static string NormModel(string model, string? make)
    {
        var m = Squash(model);
        if (make != null)
        {
            var w = FirstWord(make);
            if (m.StartsWith(w + " ", StringComparison.Ordinal)) m = m[(w.Length + 1)..];
        }
        return m;
    }
}
