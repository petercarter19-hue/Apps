using System.Diagnostics;
using System.Windows;
using ImageMagick;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 4: look-alikes on screen, the Settings window, the quit signal, the icon and the one-file app.</summary>
[Collection("WPF")]
public class Step4Tests
{
    private static readonly Meta Mine = new("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    private static void MakeBurst(Step3Env e, bool withExactCopyOfShotOne = false)
    {
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, 3, 1);
        using var s3 = TestScenes.Scene(1, 4, 2, w: 800, h: 600);
        var shot1 = TestScenes.Encode(s1, MagickFormat.Jpeg, 92, Mine);
        e.Ws.Photo("Burst\\IMG_2041.jpg", shot1);
        if (withExactCopyOfShotOne) e.Ws.Photo("Backup\\IMG_2041.jpg", shot1);
        e.Ws.Photo("Burst\\IMG_2042.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, 92, Mine with { SubSec = "41" }));
        e.Ws.Photo("Burst\\IMG_2043.jpg", TestScenes.Encode(s3, MagickFormat.Jpeg, 92, Mine with { Taken = "2026:10:12 14:03:10", SubSec = "02" }));
    }

    private static Step3Env WithLookAlikes(string name, Strictness s = Strictness.Normal)
    {
        var e = new Step3Env(name);
        Assert.Null(e.S.Folders.SetLookAlikes(true, s));
        WpfHost.Run(() => e.Vm.RefreshFolders());
        return e;
    }

    // ---------------------------------------------------------------- the tab, the headline, the note

    [Fact]
    public async Task Without_the_setting_there_is_no_look_alike_tab_and_no_look_alike_work()
    {
        using var e = new Step3Env("s4-off");
        MakeBurst(e);
        await e.ScanAsync();

        Assert.Equal(Visibility.Collapsed, e.Vm.LookAlikeTabVisibility);
        Assert.Empty(e.Vm.LookAlikeGroups);
        Assert.Equal("No duplicates found.", e.Vm.Headline);
        Assert.DoesNotContain("look-alike", e.Vm.Subline);
    }

    [Fact]
    public async Task With_the_setting_on_look_alikes_get_their_own_tab_and_are_never_in_the_headline()
    {
        using var e = WithLookAlikes("s4-on");
        MakeBurst(e);
        await e.ScanAsync();

        Assert.Equal(Visibility.Visible, e.Vm.LookAlikeTabVisibility);
        var g = Assert.Single(e.Vm.LookAlikeGroups);
        Assert.Equal("Look-alikes (1)", e.Vm.LookAlikeTabHeader);
        Assert.Equal(2, e.Vm.SelectedTab);                                    // nothing else to show, so the look-alikes tab opens
        Assert.Equal("No duplicates found.", e.Vm.Headline);                  // not counted as duplicates
        Assert.Contains("Also: 1 group of look-alikes", e.Vm.Subline);
        Assert.Equal("3 look-alike photos", g.Title);
        Assert.Contains("Different photos", g.Subtitle);
        Assert.Equal(Visibility.Visible, g.LookAlikeNoteVisibility);
        Assert.Equal("These are different photos, not copies. Look at each one before you remove any.", g.LookAlikeNote);
        Assert.Equal(Visibility.Collapsed, g.NoDateWarningVisibility);        // that line is for same-photo groups
        Assert.Equal("IMG_2043.jpg", g.Cards[0].Name);                        // the biggest picture wears the star
        Assert.True(g.Cards[0].IsKeeper);
        Assert.Empty(e.Vm.ExactGroups); Assert.Empty(e.Vm.SameGroups);
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllExactVisibility);    // no bulk anything for look-alikes
    }

    [Fact]
    public async Task Removing_the_others_of_a_look_alike_group_asks_first_and_the_safe_answer_is_the_default()
    {
        using var e = WithLookAlikes("s4-remove");
        MakeBurst(e);
        await e.ScanAsync();
        var g = e.Vm.LookAlikeGroups.Single();
        var keeper = g.Model.Keeper;
        e.Dialogs.Answer(DialogChoice.Cancel);

        await e.RemoveAsync(g);

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal("Remove look-alike photos?", ask.Title);
        Assert.Equal("These are different photos that only look alike. Remove the other 2 photos and keep the one with the star?", ask.Message);
        Assert.Equal("Remove 2 photos", ask.PrimaryText);
        Assert.Equal(DialogChoice.Cancel, ask.Default);
        Assert.Equal(3, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);

        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAsync(g);

        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
        Assert.True(File.Exists(keeper.Path));
        Assert.Single(Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Where(f => !f.Contains("Duplicates to delete")));
        Assert.Empty(e.Vm.LookAlikeGroups);                                   // the group is gone from the screen
        Assert.Equal("No duplicates found.", e.Vm.Headline);
        // and Undo brings them back
        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        Assert.Equal(3, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count(f => !f.Contains("Duplicates to delete")));
    }

    [Fact]
    public async Task Removing_a_look_alike_that_is_another_groups_keeper_updates_that_group_on_screen_and_the_headline()
    {
        using var e = WithLookAlikes("s4-cross");
        MakeBurst(e, withExactCopyOfShotOne: true);
        await e.ScanAsync();
        var exact = e.Vm.ExactGroups.Single();
        var look = e.Vm.LookAlikeGroups.Single();
        Assert.Equal("IMG_2043.jpg", look.Model.Keeper.Name);
        Assert.StartsWith("1 duplicate found.", e.Vm.Headline);
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAsync(look);

        // shot 1's keeper was one of the look-alikes that went; its backup copy is now alone, so that exact group is gone too
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Empty(e.Vm.LookAlikeGroups);
        Assert.Equal("No duplicates left.", e.Vm.Headline);
        // exactly one copy of shot 1 is left (which of the two identical files was the star depends on their timestamps)
        Assert.Single(Directory.EnumerateFiles(e.Ws.Pictures, "IMG_2041.jpg", SearchOption.AllDirectories).Where(f => !f.Contains("Duplicates to delete")));
        _ = exact;
    }

    // ---------------------------------------------------------------- the Settings window

    [Fact]
    public void The_settings_window_shows_the_saved_look_alike_setting_saves_changes_at_once_and_clears_the_results()
    {
        using var e = new Step3Env("s4-settings");
        var r = WpfHost.Run(() =>
        {
            var w = new FoldersWindow(e.S.Folders);
            var initial = (on: w.LookAlikesCheck.IsChecked == true, normal: w.StrictNormal.IsChecked == true, enabled: w.StrictnessPanel.IsEnabled);
            w.LookAlikesCheck.IsChecked = true;                       // Pete ticks "Also look for look-alikes"
            var enabledAfter = w.StrictnessPanel.IsEnabled;
            w.StrictLoose.IsChecked = true;                            // and picks Loose
            return (initial, enabledAfter, w.Changed, w.MessageText.Text);
        });

        Assert.False(r.initial.on);                                   // off by default
        Assert.True(r.initial.normal);                                // Normal is the default strictness
        Assert.False(r.initial.enabled);                              // and the strictness choices are greyed out while it is off
        Assert.True(r.enabledAfter);
        Assert.True(r.Changed);
        Assert.Equal("", r.Text);
        var saved = new SettingsStore(e.Ws.Paths, e.Ws.Log).Load();
        Assert.True(saved.LookAlikesOn);
        Assert.Equal("Loose", saved.LookAlikeStrictness);

        // reopening shows the saved choice
        var again = WpfHost.Run(() => { var w = new FoldersWindow(e.S.Folders); return (w.LookAlikesCheck.IsChecked, w.StrictLoose.IsChecked, w.StrictNormal.IsChecked); });
        Assert.Equal((true, true, false), again);
    }

    [Fact]
    public async Task Changing_the_setting_clears_old_results_so_the_next_scan_uses_it()
    {
        using var e = new Step3Env("s4-clear");
        MakeBurst(e);
        await e.ScanAsync();
        Assert.Equal("No duplicates found.", e.Vm.Headline);

        Assert.Null(e.S.Folders.SetLookAlikes(true, Strictness.Normal));
        WpfHost.Run(() => e.Vm.RefreshFolders());

        Assert.Equal("", e.Vm.Headline);
        Assert.Contains("Click Scan", e.Vm.Hint);
        await e.ScanAsync();
        Assert.Single(e.Vm.LookAlikeGroups);
    }

    [Fact]
    public void A_look_alike_setting_that_cannot_be_saved_is_not_applied_and_says_why()
    {
        using var e = new Step3Env("s4-nosave");
        File.Delete(e.Ws.Paths.SettingsFile);
        Directory.CreateDirectory(e.Ws.Paths.SettingsFile);   // a folder where the settings file should be
        var why = e.S.Folders.SetLookAlikes(true, Strictness.Strict);
        Assert.Contains("couldn't save", why);
        Assert.False(e.S.Folders.LookAlikesOn);
        Assert.Equal(Strictness.Normal, e.S.Folders.LookAlikeStrictness);
    }

    // ---------------------------------------------------------------- the quit signal and the files that make the app

    [Fact]
    public void The_installer_can_ask_a_running_copy_to_close_and_nothing_happens_when_none_is_running()
    {
        var name = "PhotoTwinsTest" + Guid.NewGuid().ToString("N");
        Assert.False(SingleInstance.RequestQuit(name));          // nobody running: nothing to close, no error

        int asked = 0;
        using var running = new SingleInstance(name, () => { }, () => Interlocked.Increment(ref asked));
        Assert.True(SingleInstance.RequestQuit(name));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, 5000), "the running copy was never asked to close");
    }

    [Fact]
    public void The_real_program_exits_cleanly_when_told_to_quit_and_nothing_is_running()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        var exe = Path.Combine(root, "src", "PhotoTwins", "bin", "Debug", "net10.0-windows", "PhotoTwins.exe");
        var psi = new ProcessStartInfo(exe, "--quit") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(30000));
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void The_app_has_its_icon_with_every_size_and_the_project_uses_it()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        var ico = Path.Combine(root, "src", "PhotoTwins", "photostwins.ico");
        var bytes = File.ReadAllBytes(ico);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));      // an icon file
        Assert.Equal(7, BitConverter.ToUInt16(bytes, 4));      // 16, 24, 32, 48, 64, 128 and 256 pixels
        Assert.Contains("<ApplicationIcon>photostwins.ico</ApplicationIcon>", File.ReadAllText(Path.Combine(root, "src", "PhotoTwins", "PhotoTwins.csproj")));
        // the built program carries it: Windows can extract a picture from the exe
        var exe = Path.Combine(root, "src", "PhotoTwins", "bin", "Debug", "net10.0-windows", "PhotoTwins.exe");
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe)!;
        Assert.True(icon.Width >= 16);
    }

    [Fact]
    public void The_install_script_builds_checks_then_swaps_and_never_kills_anything()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        var text = File.ReadAllText(Path.Combine(root, "tools", "install.ps1"));
        var order = new[] { "dotnet publish", "Test-Build $newExe", "'--quit'", "Move-Item", "Copy-Item", "CreateShortcut", "Test-Build $exe" };
        int last = -1;
        foreach (var step in order)
        {
            int at = text.IndexOf(step, last + 1, StringComparison.Ordinal);
            Assert.True(at > last, $"install.ps1: '{step}' is missing or out of order");
            last = at;
        }
        Assert.DoesNotContain("Stop-Process", text);
        Assert.DoesNotContain("taskkill", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Force -Recurse", text);
    }

    // ---------------------------------------------------------------- pictures

    [Fact]
    public async Task The_look_alike_tab_and_the_settings_window_fit_every_size_and_read_clearly()
    {
        using var e = WithLookAlikes("s4-pictures");
        MakeBurst(e, withExactCopyOfShotOne: true);
        e.Identical(300, "Beach\\IMG_3310.jpg", "Beach\\IMG_3310 copy.jpg");
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.LookAlikeGroups);
        e.Vm.SelectedTab = 2;
        ScreenShots.Take(e.Vm, "step4", "look-alike-tab", height: 1000);
        e.Vm.SelectedTab = 0;
        ScreenShots.WaitForPictures(e.Vm.ExactGroups);
        ScreenShots.Take(e.Vm, "step4", "exact-tab-with-look-alike-tab", height: 700);

        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family"));
        Directory.CreateDirectory(Path.Combine(e.Ws.Pictures, "Family"));
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                WpfHost.SetTheme(dark);
                var png = Path.Combine(ScreenShots.Folder("step4"), $"settings-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                var problems = WpfHost.Run(() =>
                {
                    var win = new FoldersWindow(e.S.Folders);
                    var content = (FrameworkElement)win.Content;
                    win.Content = null;                                   // the window itself is never shown; only its contents are drawn
                    var host = new System.Windows.Controls.Border { Child = content };
                    content.Margin = new Thickness(0);
                    SelfCheck.RenderToPng(host, 780, 840, scale, png);
                    var p = SelfCheck.FindCutOffText(host);
                    p.AddRange(SelfCheck.FindLowContrastText(host, SelfCheck.PageBackground()));
                    p.AddRange(GlassChecks.WorstCase(host));
                    return p;
                });
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 5000);
                Assert.True(problems.Count == 0, $"Settings: text cut off or hard to read ({(dark ? "dark" : "light")}, {scale * 100}%): " + string.Join(" | ", problems));
            }
        WpfHost.SetTheme(false);
    }
}
