using System.Reflection;
using System.Text.RegularExpressions;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The folder list Pete sees: starts with Pictures, remembered, every pick checked.</summary>
public class FolderListTests
{
    private static (FolderList list, SettingsStore store, AppSettings settings) Make(TestWorkspace ws)
    {
        var store = new SettingsStore(ws.Paths, ws.Log);
        var s = store.Load();
        return (new FolderList(s, store, ws.Fs, ws.Policy), store, s);
    }

    [Fact]
    public void First_run_starts_with_the_pictures_folder_and_remembers_it()
    {
        using var ws = new TestWorkspace("fl-first");
        var (list, store, _) = Make(ws);
        list.EnsureDefaults(ws.Pictures);
        Assert.Equal(new[] { ScanPolicy.Normalize(ws.Pictures) }, list.Folders);

        var reloaded = new FolderList(store.Load(), store, ws.Fs, ws.Policy);
        Assert.Equal(new[] { ScanPolicy.Normalize(ws.Pictures) }, reloaded.Folders);
    }

    [Fact]
    public void A_folder_removed_stays_removed_next_run_pictures_is_not_added_back()
    {
        using var ws = new TestWorkspace("fl-removed");
        var (list, store, _) = Make(ws);
        list.EnsureDefaults(ws.Pictures);
        list.Remove(ws.Pictures);

        var again = new FolderList(store.Load(), store, ws.Fs, ws.Policy);
        again.EnsureDefaults(ws.Pictures);

        Assert.Empty(again.Folders);
    }

    [Fact]
    public void First_run_does_not_offer_a_pictures_folder_that_is_not_allowed()
    {
        using var ws = new TestWorkspace("fl-notallowed");
        var (list, _, _) = Make(ws);
        list.EnsureDefaults(Path.Combine(ws.FakeGoogleDrive, "Pictures"));   // does not exist and is on the pretend Google Drive
        Assert.Empty(list.Folders);
    }

    [Fact]
    public void Picks_are_checked_and_the_reason_is_plain_english()
    {
        using var ws = new TestWorkspace("fl-picks");
        var (list, _, _) = Make(ws);
        var gone = Path.Combine(ws.Root, "not-there");
        Assert.Contains("doesn't exist", list.TryAdd(gone));
        Assert.Contains("another person", list.TryAdd(ws.Other));
        Assert.Contains("Google Drive", list.TryAdd(ws.FakeGoogleDrive));
        Assert.Contains("full folder", list.TryAdd("Pictures"));
        Assert.Contains("Pick a folder", list.TryAdd("  "));
        Assert.Empty(list.Folders);
    }

    [Fact]
    public void Adding_the_same_or_an_inner_folder_says_it_is_already_covered_and_a_bigger_one_absorbs_the_smaller()
    {
        using var ws = new TestWorkspace("fl-nest");
        var inner = Path.Combine(ws.Pictures, "Trips"); Directory.CreateDirectory(inner);
        var (list, store, _) = Make(ws);

        Assert.Null(list.TryAdd(inner));
        Assert.Contains("already in the list", list.TryAdd(inner.ToUpperInvariant()));
        Assert.Null(list.TryAdd(ws.Pictures));                      // bigger: absorbs "Trips"
        Assert.Equal(new[] { ScanPolicy.Normalize(ws.Pictures) }, list.Folders);
        Assert.Contains("already covered", list.TryAdd(inner));

        Assert.Equal(new[] { ScanPolicy.Normalize(ws.Pictures) }, store.Load().Folders);
    }

    [Fact]
    public void A_saved_folder_that_has_since_disappeared_is_flagged_not_forgotten()
    {
        using var ws = new TestWorkspace("fl-gone");
        var usb = Path.Combine(ws.Root, "usb-stick"); Directory.CreateDirectory(usb);
        var (list, _, _) = Make(ws);
        Assert.Null(list.TryAdd(usb));
        Directory.Delete(usb);

        var d = Assert.Single(list.Describe());
        Assert.Contains("doesn't exist", d.Problem);
        Assert.Single(list.Folders);   // kept, so it works again when the drive comes back
    }

    [Fact]
    public void Damaged_settings_are_kept_aside_and_the_screen_is_told()
    {
        using var ws = new TestWorkspace("fl-damaged");
        File.WriteAllText(ws.Paths.SettingsFile, "{ this is not json");
        var store = new SettingsStore(ws.Paths, ws.Log);

        var s = store.Load();

        Assert.NotNull(store.LoadProblem);
        Assert.True(File.Exists(ws.Paths.SettingsFile + ".damaged"));
        Assert.Empty(s.Folders);
        Assert.Contains("Settings file unreadable", File.ReadAllText(ws.Log.CurrentFile));
    }
}

/// <summary>Rules about the code itself: S-NET (nothing leaves this computer) and S-ONEPATH (only one place may change files).</summary>
public class CodeRuleTests
{
    private static string SrcDir()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        return Path.Combine(root, "src");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"));

    [Fact]
    public void S_NET_the_app_has_no_network_code()
    {
        string[] banned = { "System.Net", "HttpClient", "WebClient", "WebRequest", "TcpClient", "UdpClient", "Socket", "WebSocket" };
        foreach (var f in SourceFiles())
        {
            var text = File.ReadAllText(f);
            foreach (var b in banned)
                Assert.False(text.Contains(b), $"{Path.GetFileName(f)} mentions {b}");
        }
    }

    [Fact]
    public void S_NET_the_built_app_does_not_reference_networking_libraries()
    {
        foreach (var name in new[] { "PhotoTwins.Core", "PhotoTwins" })
        {
            var asm = Assembly.Load(name);
            var refs = asm.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(refs, r => r.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Files allowed to write or delete, and why. Photos are never among them.
    private static readonly Dictionary<string, string> MayChangeOwnFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AtomicFile.cs"] = "saves the app's own settings",
        ["AppLog.cs"] = "writes and tidies the app's own log",
        ["AppSettings.cs"] = "keeps a copy of a damaged settings file",
        ["PhotoCache.cs"] = "sets aside a damaged cache",
        ["ThumbnailStore.cs"] = "sets aside a damaged picture memory",
        ["SelfCheck.cs"] = "writes the app's own picture of itself",
        ["UndoJournal.cs"] = "writes the app's own record of removals",
        ["FileMutations.cs"] = "THE one place that moves photos (never deletes them)",
        ["ShellRecycle.cs"] = "sends a photo to the Recycle Bin, with a guard against permanent deletes (used only by FileMutations)",
    };

    [Fact]
    public void S_ONEPATH_no_code_changes_photo_files_except_the_one_gate()
    {
        var patterns = new[]
        {
            @"File\.(Delete|Move|Copy|Replace|Create|WriteAll\w*|AppendAll\w*|SetAttributes|SetLastWrite\w*|SetCreation\w*|OpenWrite)\(",
            @"Directory\.(Delete|Move)\(", @"FileMode\.(Create|CreateNew|Truncate|OpenOrCreate|Append)", @"FileAccess\.(Write|ReadWrite)",
            @"SHFileOperation", @"IFileOperation", @"MoveFileEx", @"DeleteFile", @"\.Delete\(\)", @"\.MoveTo\(", @"\.CopyTo\(",
        };
        foreach (var f in SourceFiles())
        {
            var name = Path.GetFileName(f);
            if (MayChangeOwnFiles.ContainsKey(name)) continue;
            var text = File.ReadAllText(f);
            foreach (var p in patterns)
                Assert.False(Regex.IsMatch(text, p), $"{name} changes files ({p}); only the gate may, and it doesn't exist yet in step 1");
        }
    }

    [Fact]
    public void S_BIN_no_code_anywhere_can_delete_a_photo_for_good()
    {
        // Own-file tidying (old log files) is the only delete in the whole program. Photos can only be recycled or renamed.
        var patterns = new[] { @"File\.Delete\(", @"Directory\.Delete\(", @"\.Delete\(\)", @"SHFileOperation", @"DeleteFile", @"DeleteItems\(", @"RemoveDirectory", @"FileMode\.Truncate" };
        foreach (var f in SourceFiles())
        {
            var name = Path.GetFileName(f);
            if (name == "AppLog.cs") continue;   // deletes its own old log files
            var text = File.ReadAllText(f);
            foreach (var p in patterns)
            {
                if (name == "ShellRecycle.cs" && p.StartsWith("DeleteItems")) continue;
                if (name == "FileMutations.cs" && p.StartsWith("Directory")) continue;   // the one tidy-up of EMPTY holding sub-folders, checked below   // (only the declaration Windows' interface needs: never called)
                Assert.False(Regex.IsMatch(text, p), $"{name} contains {p}: photos must never be deleted for good");
            }
        }
        // the only folder delete anywhere removes an EMPTY folder inside the holding folder, and nothing else
        var mutations = File.ReadAllText(SourceFiles().Single(f => Path.GetFileName(f) == "FileMutations.cs"));
        var deletes = Regex.Matches(mutations, @"Directory\.Delete\(([^)]*)\)");
        Assert.Single(deletes);
        Assert.EndsWith(", false", deletes[0].Groups[1].Value);                 // never recursive
        Assert.Contains("EnumerateFileSystemEntries(dir).Any()) break;", mutations);   // only after checking it is empty
        // the one place that asks Windows to delete (to the Recycle Bin) carries the guard against permanent deletes
        var recycle = File.ReadAllText(SourceFiles().Single(f => Path.GetFileName(f) == "ShellRecycle.cs"));
        Assert.Contains("FOF_ALLOWUNDO", recycle);
        Assert.Contains("TSF_DELETE_RECYCLE_IF_POSSIBLE", recycle);
        Assert.Single(Regex.Matches(recycle, @"op\.DeleteItem\("));
    }

    [Fact]
    public void S_ONEPATH_only_FileMutations_talks_to_the_Recycle_Bin_or_moves_photos()
    {
        foreach (var f in SourceFiles())
        {
            var name = Path.GetFileName(f);
            if (name is "FileMutations.cs" or "ShellRecycle.cs") continue;
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("ShellRecycle.", text);
            Assert.DoesNotContain("MoveFileEx", text);
            Assert.DoesNotContain("IFileOperation", text);
        }
    }

    [Fact]
    public void S_CLICK_a_removal_can_only_start_from_the_button_handlers_in_the_main_view_model()
    {
        foreach (var f in SourceFiles())
        {
            var name = Path.GetFileName(f);
            var text = File.ReadAllText(f);
            if (name is not ("MainViewModel.cs" or "RemovalPlan.cs"))
            {
                Assert.DoesNotContain("RemovalPlan.ForGroup(", text);
                Assert.DoesNotContain("RemovalPlan.ForAllExactCopies(", text);
                Assert.DoesNotContain("RemovalPlan.ForAllSamePhoto(", text);
            }
            if (name is not ("MainViewModel.cs" or "RemovalService.cs"))
                Assert.DoesNotContain("Removal.Execute(", text);
            if (name is not ("UndoViewModel.cs" or "FileMutations.cs"))
                Assert.DoesNotContain(".PutBack(", text);
        }
        // and within the main view model, the plan is built only inside the public removal methods that the buttons call (one group, all exact copies, all same-photo copies)
        var vm = File.ReadAllText(SourceFiles().Single(f => Path.GetFileName(f) == "MainViewModel.cs"));
        Assert.Single(Regex.Matches(vm, @"RemovalPlan\.ForGroup\("));
        Assert.Single(Regex.Matches(vm, @"RemovalPlan\.ForAllExactCopies\("));
        Assert.Single(Regex.Matches(vm, @"RemovalPlan\.ForAllSamePhoto\("));
        Assert.Single(Regex.Matches(vm, @"_s\.Removal\.Execute\("));
    }

    [Fact]
    public void S_ONEPATH_the_scanner_only_gets_a_read_only_file_system()
    {
        // The interface the scan uses has no write, move or delete: the compiler enforces it.
        var names = typeof(IFileSystem).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "DirectoryExists", "Enumerate", "GetAttributes", "GetRealPath", "IsLink", "OpenRead" }, names);
    }
}
