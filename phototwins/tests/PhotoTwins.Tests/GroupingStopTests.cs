using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class GroupingStopTests
{
    [Fact]
    public void Stopping_at_grouping_leaves_the_scan_unfinished_for_continue()
    {
        using var ws = new TestWorkspace("stop-grouping");
        var bytes = TestPhotoFactory.Jpeg(1);
        ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var control = new ScanControl();
        var progress = new SyncProgress<ScanProgress>(p => { if (p.Phase == ScanPhase.Grouping) control.Stop(); });

        var result = ws.NewEngine().Run(new[] { ws.Pictures }, control, progress);

        Assert.False(result.Completed);
        Assert.True(ws.Cache.LastScanUnfinished);
        Assert.Equal(2, result.PhotosFound);
    }
}
