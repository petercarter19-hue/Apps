using System.Windows;

namespace PhotoTwins;

/// <summary>One group in a row of the list, and how wide it is drawn.</summary>
public sealed class GroupSlot : ObservableObject
{
    private double _width;
    private Thickness _margin;

    public GroupSlot(GroupVm group) => Group = group;

    public GroupVm Group { get; }
    public double Width { get => _width; set => Set(ref _width, value); }
    public Thickness Margin { get => _margin; set => Set(ref _margin, value); }
}

/// <summary>One row of the group list: small groups sit side by side so the page has no big empty space beside them.</summary>
public sealed class GroupRow
{
    public GroupRow(IReadOnlyList<GroupSlot> slots) => Slots = slots;
    public IReadOnlyList<GroupSlot> Slots { get; }
}

/// <summary>
/// Packs the groups, in their order, into rows as wide as the list: as many groups side by side as fit, each row then stretched to the full
/// width (a group alone in its row uses the full width). The list stays virtualized: only the rows on screen are built.
/// </summary>
public static class GroupRows
{
    public const double PhotoSlot = 278;     // one photo card (264) and the space after it (14)
    public const double Chrome = 34;         // the group card's padding and edge
    public const double MinGroup = 440;      // room for the title and the Remove button
    public const double Gap = 16;            // between groups in a row

    /// <summary>How wide a group needs to be to show all its photos in one line.</summary>
    public static double Needs(int photos) => Math.Max(MinGroup, Chrome + photos * PhotoSlot);

    /// <summary>Which groups go in which row (indexes, in order), for the given photo counts and list width.</summary>
    public static List<List<int>> Pack(IReadOnlyList<int> photoCounts, double width)
    {
        var rows = new List<List<int>>();
        List<int>? row = null;
        double used = 0;
        for (int i = 0; i < photoCounts.Count; i++)
        {
            double need = Needs(photoCounts[i]);
            if (row != null && used + Gap + need <= width) { row.Add(i); used += Gap + need; continue; }
            row = new List<int> { i };
            rows.Add(row);
            used = need;
        }
        return rows;
    }

    /// <summary>
    /// The widths of the groups in one row: a group alone takes the whole width; several share the whole width in proportion to what each
    /// needs, so the row ends exactly at the right edge.
    /// </summary>
    public static double[] Widths(IReadOnlyList<int> photoCounts, double width)
    {
        if (photoCounts.Count == 1) return new[] { Math.Max(0, width) };
        var needs = photoCounts.Select(Needs).ToArray();
        double room = width - Gap * (photoCounts.Count - 1);
        double sum = needs.Sum();
        return needs.Select(n => Math.Floor(room * n / sum)).ToArray();
    }

    /// <summary>The rows for the list, reusing <paramref name="previous"/> (only the widths change) when the same groups land in the same rows.</summary>
    public static IReadOnlyList<GroupRow> Build(IReadOnlyList<GroupVm> groups, double width, IReadOnlyList<GroupRow>? previous)
    {
        var counts = groups.Select(g => g.Model.Files.Count).ToList();
        var packed = Pack(counts, width);
        bool same = previous != null && previous.Count == packed.Count
                    && packed.Zip(previous).All(p => p.First.Count == p.Second.Slots.Count
                                                     && p.First.Zip(p.Second.Slots).All(q => ReferenceEquals(groups[q.First], q.Second.Group)));
        var rows = same ? previous! : packed.Select(r => new GroupRow(r.Select(i => new GroupSlot(groups[i])).ToList())).ToList();
        for (int r = 0; r < packed.Count; r++)
        {
            var w = Widths(packed[r].Select(i => counts[i]).ToList(), width);
            for (int k = 0; k < w.Length; k++)
            {
                rows[r].Slots[k].Width = w[k];
                rows[r].Slots[k].Margin = new Thickness(0, 0, k < w.Length - 1 ? Gap : 0, 0);
            }
        }
        return rows;
    }
}
