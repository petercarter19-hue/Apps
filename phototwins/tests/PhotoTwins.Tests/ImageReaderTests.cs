using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Can the free image reader and the free photo-info reader handle every type Pete listed?</summary>
public class ImageReaderTests
{
    public static IEnumerable<object[]> Types() => new[]
    {
        new object[] { MagickFormat.Jpeg }, new object[] { MagickFormat.Png }, new object[] { MagickFormat.WebP },
        new object[] { MagickFormat.Bmp }, new object[] { MagickFormat.Tiff },
    };

    [Theory, MemberData(nameof(Types))]
    public void Every_type_can_be_written_by_the_test_factory_and_read_back(MagickFormat f)
    {
        Assert.True(TestScenes.CanWrite(f), $"the test factory can't write {f}");
        using var scene = TestScenes.Scene(1);
        var bytes = TestScenes.Encode(scene, f);
        using var back = new MagickImage(bytes);
        Assert.Equal((uint)TestScenes.W, back.Width);
        Assert.Equal((uint)TestScenes.H, back.Height);
    }

    [Fact]
    public void Photo_info_written_by_the_factory_is_read_back_by_the_info_reader_for_the_types_that_carry_it()
    {
        var meta = new Meta("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", "SN123", "UID456", Gps: true);
        var report = new List<string>();
        foreach (var f in new[] { MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.WebP, MagickFormat.Tiff })
        {
            using var scene = TestScenes.Scene(1);
            var bytes = TestScenes.Encode(scene, f, meta: meta);
            string line;
            try
            {
                var dirs = ImageMetadataReader.ReadMetadata(new MemoryStream(bytes));
                var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
                var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
                line = $"{f}: taken={sub?.GetString(ExifDirectoryBase.TagDateTimeOriginal)} sub={sub?.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal)} make={ifd0?.GetString(ExifDirectoryBase.TagMake)} model={ifd0?.GetString(ExifDirectoryBase.TagModel)} serial={sub?.GetString(ExifDirectoryBase.TagBodySerialNumber)} uid={sub?.GetString(ExifDirectoryBase.TagImageUniqueId)} gps={gps?.GetGeoLocation() != null}";
            }
            catch (Exception ex) { line = $"{f}: info reader failed: {ex.Message}"; }
            report.Add(line);
        }
        var dir = Path.Combine(TestWorkspace.TestWorkRoot(), "probe");
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "info-roundtrip.txt"), report);
        Assert.Contains("taken=2026:10:12 14:03:09", report[0]);   // JPEG must always work
        Assert.Contains("make=Apple", report[0]);
        Assert.Contains("gps=True", report[0]);
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Heic_photos_can_be_read_and_their_info_too()
    {
        using var img = new MagickImage(File.ReadAllBytes(Fixture("scene1-with-info.heic")));
        Assert.Equal((uint)TestScenes.W, img.Width);
        Assert.Equal((uint)TestScenes.H, img.Height);
        var dirs = ImageMetadataReader.ReadMetadata(Fixture("scene1-with-info.heic"));
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        Assert.Equal("Apple", ifd0?.GetString(ExifDirectoryBase.TagMake));
        Assert.Equal("2026:10:12 14:03:09", sub?.GetString(ExifDirectoryBase.TagDateTimeOriginal));
        Assert.NotNull(dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation());
    }

    /// <summary>
    /// Only used by hand to (re)make the HEIC test files: the free image reader can read HEIC but cannot write it, so
    /// tools\make-heic-fixtures.py makes them once from these made-up scenes. Does nothing unless asked.
    /// </summary>
    [Fact]
    public void Heic_fixture_sources_are_exported_only_when_asked()
    {
        var target = Environment.GetEnvironmentVariable("PHOTOTWINS_FIXTURE_SOURCES");
        if (string.IsNullOrEmpty(target)) return;
        Assert.StartsWith(TestWorkspace.TestWorkRoot(), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);   // test photos only ever go under test-work
        System.IO.Directory.CreateDirectory(target);
        using var scene = TestScenes.Scene(1);
        File.WriteAllBytes(Path.Combine(target, "scene1.png"), TestScenes.Encode(scene, MagickFormat.Png));
    }
}
