using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class UndoLinkTests
{
    [Fact]
    public void Legacy_held_photo_with_changed_contents_is_left_untouched()
    {
        using var h = new RemovalHarness("undo-legacy-changed", new NoBinRules());
        h.Identical(918, "a.jpg", "b.jpg");
        var entry = Assert.Single(h.Run(h.PlanFor(Assert.Single(h.Scan().Decided))).Removed) with
            { SelectedRoot = null, RealRoot = null, OriginalRealPath = null, OriginalIdentity = null };
        File.WriteAllText(entry.Destination!, "a different synthetic photo");

        var result = h.Mutations.PutBack(entry);

        Assert.False(result.Ok);
        Assert.True(File.Exists(entry.Destination));
        Assert.False(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public void Undo_does_not_move_a_replacement_file_at_the_holding_path()
    {
        using var h = new RemovalHarness("undo-file-replaced", new NoBinRules());
        h.Identical(917, "a.jpg", "b.jpg");
        var entry = Assert.Single(h.Run(h.PlanFor(Assert.Single(h.Scan().Decided))).Removed);
        var saved = Path.Combine(h.Ws.Outside, "saved.jpg");
        File.Move(entry.Destination!, saved);
        File.Copy(saved, entry.Destination!); // same contents, but not the file that was removed

        var result = h.Mutations.PutBack(entry);

        Assert.False(result.Ok);
        Assert.True(File.Exists(saved));
        Assert.True(File.Exists(entry.Destination));
        Assert.False(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public void Undo_refuses_when_the_original_parent_was_replaced_by_a_junction()
    {
        using var h = new RemovalHarness("undo-dest-link", new NoBinRules());
        var paths = h.Identical(903, "a.jpg", "Trip\\b.jpg");
        var group = Assert.Single(h.Scan().Decided);
        Assert.True(group.TrySetKeeper(group.Files.Single(f => f.Path == paths[0])));
        var entry = Assert.Single(h.Run(h.PlanFor(group)).Removed);
        var parent = Path.GetDirectoryName(entry.OriginalPath)!;
        Directory.Delete(parent);
        h.Ws.MakeJunction(parent, h.Ws.Outside);

        var result = h.Mutations.PutBack(entry);

        Assert.False(result.Ok);
        Assert.True(File.Exists(entry.Destination));
        Assert.Empty(Directory.EnumerateFiles(h.Ws.Outside));
        Assert.False(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public void Undo_refuses_when_the_holding_parent_was_replaced_by_a_junction()
    {
        using var h = new RemovalHarness("undo-source-link", new NoBinRules());
        var paths = h.Identical(904, "a.jpg", "Trip\\b.jpg");
        var group = Assert.Single(h.Scan().Decided);
        Assert.True(group.TrySetKeeper(group.Files.Single(f => f.Path == paths[0])));
        var entry = Assert.Single(h.Run(h.PlanFor(group)).Removed);
        var parent = Path.GetDirectoryName(entry.Destination!)!;
        var outsideFile = Path.Combine(h.Ws.Outside, Path.GetFileName(entry.Destination!));
        File.Move(entry.Destination!, outsideFile);
        Directory.Delete(parent);
        h.Ws.MakeJunction(parent, h.Ws.Outside);

        var result = h.Mutations.PutBack(entry);

        Assert.False(result.Ok);
        Assert.True(File.Exists(outsideFile));
        Assert.False(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public void Undo_allows_a_stable_selected_root_alias_but_refuses_if_it_is_retargeted()
    {
        using var h = new RemovalHarness("undo-root-alias", new NoBinRules());
        h.Identical(905, "a.jpg", "b.jpg", "c.jpg");
        var alias = Path.Combine(h.Ws.Root, "Alias");
        h.Ws.MakeJunction(alias, h.Ws.Pictures);
        var group = Assert.Single(h.Scan(alias).Decided);
        var removed = h.Run(PhotoTwins.Core.Removal.RemovalPlan.ForGroup(group, new[] { alias })).Removed;
        Assert.Equal(2, removed.Count);
        Assert.True(h.Mutations.PutBack(removed[0]).Ok);
        Directory.Delete(alias);
        h.Ws.MakeJunction(alias, h.Ws.Outside);

        var result = h.Mutations.PutBack(removed[1]);

        Assert.False(result.Ok);
        Assert.True(File.Exists(removed[1].Destination!.Replace(alias, h.Ws.Pictures, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(Directory.EnumerateFiles(h.Ws.Outside));
    }

    [Fact]
    public void A_legacy_plain_path_can_still_be_restored_but_a_legacy_link_is_refused()
    {
        using var h = new RemovalHarness("undo-legacy", new NoBinRules());
        var paths = h.Identical(906, "a.jpg", "Trip\\b.jpg", "Trip\\c.jpg");
        var group = Assert.Single(h.Scan().Decided);
        Assert.True(group.TrySetKeeper(group.Files.Single(f => f.Path == paths[0])));
        var removed = h.Run(h.PlanFor(group)).Removed;
        Assert.Equal(2, removed.Count);
        var first = removed[0] with { SelectedRoot = null, RealRoot = null, OriginalRealPath = null, OriginalIdentity = null };
        var firstResult = h.Mutations.PutBack(first);
        Assert.True(firstResult.Ok, firstResult.Message);
        var second = removed[1] with { SelectedRoot = null, RealRoot = null, OriginalRealPath = null, OriginalIdentity = null };
        var parent = Path.GetDirectoryName(second.Destination!)!;
        var outsideFile = Path.Combine(h.Ws.Outside, Path.GetFileName(second.Destination!));
        File.Move(second.Destination!, outsideFile);
        Directory.Delete(parent);
        h.Ws.MakeJunction(parent, h.Ws.Outside);

        Assert.False(h.Mutations.PutBack(second).Ok);
        Assert.True(File.Exists(outsideFile));
        Assert.False(File.Exists(second.OriginalPath));
    }
}
