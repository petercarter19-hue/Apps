using PhotoTwins.Core.Removal;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class HoldingFolderLinkTests
{
    [Fact]
    public void Holding_directory_cannot_be_replaced_between_final_check_and_move()
    {
        using var h = new RemovalHarness("holding-race", new NoBinRules());
        var paths = h.Identical(916, "a.jpg", "b.jpg");
        var group = Assert.Single(h.Scan().Decided);
        var holding = Path.Combine(h.Ws.Pictures, FileMutations.HoldingFolderName);
        var moved = Path.Combine(h.Ws.Pictures, "holding-renamed");
        int visits = 0;
        bool replacementBlocked = false;
        h.Ws.Fs.AfterGetRealPath = p =>
        {
            if (!string.Equals(p, holding, StringComparison.OrdinalIgnoreCase) || ++visits != 2) return;
            try { Directory.Move(holding, moved); h.Ws.MakeJunction(holding, h.Ws.Outside); }
            catch (IOException) { replacementBlocked = true; }
        };

        var result = h.Run(h.PlanFor(group));

        Assert.True(replacementBlocked, "A directory replacement was possible immediately after its check.");
        Assert.Empty(Directory.EnumerateFiles(h.Ws.Outside));
        Assert.Single(result.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removal_refuses_a_holding_folder_junction_that_leads_outside_the_picked_root(bool nested)
    {
        using var h = new RemovalHarness("holding-link", new NoBinRules());
        var paths = h.Identical(902, "Trip\\a.jpg", "Trip\\b.jpg");
        var group = Assert.Single(h.Scan().Decided);
        var holding = Path.Combine(h.Ws.Pictures, FileMutations.HoldingFolderName);
        if (nested) Directory.CreateDirectory(holding);
        h.Ws.MakeJunction(nested ? Path.Combine(holding, "Trip") : holding, h.Ws.Outside);

        var result = h.Run(h.PlanFor(group));

        Assert.Empty(result.Removed);
        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        Assert.Empty(Directory.EnumerateFiles(h.Ws.Outside, "*", SearchOption.AllDirectories));
        Assert.NotEmpty(result.Failed);
    }
}
