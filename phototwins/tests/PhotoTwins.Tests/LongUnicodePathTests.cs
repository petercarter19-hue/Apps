using PhotoTwins.Tests.Support;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Tests;

public class LongUnicodePathTests
{
    [Theory]
    [InlineData(@"C:\folder\..\photo.jpg", @"\\?\C:\photo.jpg")]
    [InlineData(@"\\server\share\folder\photo.jpg", @"\\?\UNC\server\share\folder\photo.jpg")]
    [InlineData(@"\\?\UNC\server\share\folder\photo.jpg", @"\\?\UNC\server\share\folder\photo.jpg")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\folder", @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\folder")]
    [InlineData(@"\\.\PhysicalDrive0", @"\\.\PhysicalDrive0")]
    public void Native_paths_preserve_device_and_extended_names_and_convert_dos_and_unc_names(string path, string expected) =>
        Assert.Equal(expected, NativePath.Extended(path));

    [Theory]
    [InlineData(@"\\?\C:\folder\photo.jpg", @"C:\folder\photo.jpg")]
    [InlineData(@"\\?\UNC\server\share\photo.jpg", @"\\server\share\photo.jpg")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\folder", @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\folder")]
    public void Native_results_preserve_volume_guid_names_and_normalize_dos_and_unc_names(string path, string expected) =>
        Assert.Equal(expected, NativePath.NormalizedResult(path));

    [Fact]
    public void Native_resolution_accepts_the_same_synthetic_file_with_normal_or_extended_path()
    {
        using var ws = new TestWorkspace("extended-final-path");
        var file = ws.Photo("旅\\café.jpg", TestPhotoFactory.Jpeg(919));
        Assert.Equal(FinalPath.Of(file), FinalPath.Of(NativePath.Extended(file)), ignoreCase: true);
    }

    [Fact]
    public void Unicode_and_long_photo_paths_scan_remove_and_restore_without_changing_the_keeper()
    {
        using var h = new RemovalHarness("unicode-long", new NoBinRules());
        var longFolder = string.Join("\\", Enumerable.Range(0, 6).Select(i => $"旅-{i}-" + new string('x', 34)));
        var paths = h.Identical(915, "原本.jpg", longFolder + "\\café-🌄.jpg");
        Assert.True(paths[1].Length > 260);
        var group = Assert.Single(h.Scan().Decided);
        Assert.True(group.TrySetKeeper(group.Files.Single(f => f.Path == paths[0])));
        var keeperBefore = FileSnapshot.Of(paths[0]);
        var victimBefore = FileSnapshot.Of(paths[1]);

        var entry = Assert.Single(h.Run(h.PlanFor(group)).Removed);
        Assert.False(File.Exists(paths[1]));
        Assert.True(h.Mutations.PutBack(entry).Ok);
        Assert.Equal(keeperBefore, FileSnapshot.Of(paths[0]));
        Assert.Equal(victimBefore.Sha, FileSnapshot.Of(paths[1]).Sha);
    }
}
