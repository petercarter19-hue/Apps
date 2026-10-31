using System.Text;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Scanning;
using Microsoft.Data.Sqlite;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class XmpFractionTests
{
    private static byte[] WithDate(string date)
    {
        var jpg = TestPhotoFactory.Jpeg(42);
        var xml = $"<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:exif='http://ns.adobe.com/exif/1.0/' exif:DateTimeOriginal='{date}'/></rdf:RDF></x:xmpmeta>";
        var payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xml);
        var len = payload.Length + 2;
        return jpg.Take(2).Concat(new byte[] { 0xff, 0xe1, (byte)(len >> 8), (byte)len }).Concat(payload).Concat(jpg.Skip(2)).ToArray();
    }

    [Fact]
    public void Xmp_fractional_capture_times_distinguish_burst_photos_at_written_precision()
    {
        var first = PhotoInfoReader.Read(WithDate("2026-10-12T14:03:09.07-05:00"));
        var second = PhotoInfoReader.Read(WithDate("2026-10-12T14:03:09.41-05:00"));
        var longer = PhotoInfoReader.Read(WithDate("2026-10-12T14:03:09.0700-05:00"));
        var zeros = PhotoInfoReader.Read(WithDate("2026-10-12T14:03:09.00-05:00"));
        Assert.Equal("07", first.SubSec);
        Assert.Equal("41", second.SubSec);
        Assert.Equal("00", zeros.SubSec);
        Assert.True(InfoRules.Disagree(first, second));
        Assert.False(InfoRules.Disagree(first, longer));
        Assert.True(InfoRules.Disagree(first, zeros));
    }

    [Fact]
    public void Old_cached_dates_without_fraction_are_reexamined_after_upgrade()
    {
        using var ws = new TestWorkspace("xmp-cache-upgrade");
        ws.Photo("a.jpg", WithDate("2026-10-12T14:03:09.00-05:00"));
        using (var cache = new PhotoCache(ws.Paths.CacheFile, ws.Log))
            new ScanEngine(ws.Fs, cache, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);
        using (var db = new SqliteConnection($"Data Source={ws.Paths.CacheFile};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE files SET subsec=NULL; PRAGMA user_version=2;";
            cmd.ExecuteNonQuery();
        }
        while (ws.Fs.Opened.TryDequeue(out _)) { }
        using var upgraded = new PhotoCache(ws.Paths.CacheFile, ws.Log);
        new ScanEngine(ws.Fs, upgraded, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);

        Assert.NotEmpty(ws.Fs.Opened);
        Assert.Equal("00", Assert.Single(upgraded.LoadAll()).Value.Analysis!.Info.SubSec);
    }

    [Fact]
    public void Old_cached_nonnull_fraction_is_reexamined_when_xmp_capture_times_differ()
    {
        using var ws = new TestWorkspace("xmp-cache-stale-fraction");
        var first = ws.Photo("a.jpg", WithDate("2026-10-12T14:03:09.12-05:00"));
        var second = ws.Photo("b.jpg", WithDate("2026-10-12T14:03:09.41-05:00"));
        using (var cache = new PhotoCache(ws.Paths.CacheFile, ws.Log))
            new ScanEngine(ws.Fs, cache, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);
        // A v2 reader could combine an XMP date with an unrelated EXIF fraction; the cache cannot identify its source.
        using (var db = new SqliteConnection($"Data Source={ws.Paths.CacheFile};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE files SET subsec='07'; PRAGMA user_version=2;";
            cmd.ExecuteNonQuery();
        }
        while (ws.Fs.Opened.TryDequeue(out _)) { }
        using var upgraded = new PhotoCache(ws.Paths.CacheFile, ws.Log);
        var result = new ScanEngine(ws.Fs, upgraded, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);

        Assert.Empty(result.SameGroups);
        Assert.Contains(first, ws.Fs.Opened);
        Assert.Contains(second, ws.Fs.Opened);
        var rows = upgraded.LoadAll();
        Assert.Equal("12", rows[first].Analysis!.Info.SubSec);
        Assert.Equal("41", rows[second].Analysis!.Info.SubSec);
        while (ws.Fs.Opened.TryDequeue(out _)) { }
        var again = new ScanEngine(ws.Fs, upgraded, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);
        Assert.Equal(2, again.FromCache);
        Assert.Empty(ws.Fs.Opened);
    }
}
