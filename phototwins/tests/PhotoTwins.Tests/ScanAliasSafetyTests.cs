using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class ScanAliasSafetyTests
{
    [Fact]
    public void Scanning_a_selected_alias_to_a_users_parent_never_enters_another_profile()
    {
        using var ws = new TestWorkspace("alias-users");
        ws.Photo("mine.jpg", TestPhotoFactory.Jpeg(1));
        TestPhotoFactory.Write(Path.Combine(ws.Other, "private.jpg"), TestPhotoFactory.Jpeg(2));
        var alias = Path.Combine(ws.Root, "UsersAlias");
        ws.MakeJunction(alias, Path.Combine(ws.Root, "Users"));

        var result = ws.Scan(alias);

        Assert.Equal(1, result.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p.Contains("someone-else", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.LeftOut, p => p.Path.Contains("someone-else", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Picking_appdata_or_its_descendant_is_rejected(bool descendant)
    {
        using var ws = new TestWorkspace("pick-appdata");
        var dir = Path.Combine(ws.Me, "AppData", descendant ? "Roaming" : "");
        Directory.CreateDirectory(dir);
        TestPhotoFactory.Write(Path.Combine(dir, "icon.png"), TestPhotoFactory.Png(2));

        Assert.False(ScanRoots.Check(dir, ws.Fs, ws.Policy).Ok);
        Assert.Equal(0, ws.Scan(dir).PhotosFound);
        Assert.Empty(ws.Fs.Opened);
    }
}
