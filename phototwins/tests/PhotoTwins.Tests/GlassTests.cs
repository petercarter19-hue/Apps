using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

using ImageMagick;
using ImageMagick.Drawing;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// The glossy look (the user 10-29): a black band fading down to white, glass cards over the fade, rounded glossy buttons with grain and shadows,
/// readable everywhere, photos never covered, Windows' plain colors with high contrast. Made-up photos only.
/// </summary>
[Collection("WPF")]
public class GlassTests
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out uint value, int size);

    private static byte Chroma(Color c) => (byte)(Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)));

    private static double Lum(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    /// <summary>Runs with the look forced on or off, then puts the test default (on) back.</summary>
    private static T With<T>(bool glass, Func<T> f)
    {
        var before = PhotoTwins.Glass.Forced;
        WpfHost.Run(() => { PhotoTwins.Glass.Forced = glass; PhotoTwins.Glass.Refresh(true); });
        try { return f(); }
        finally { WpfHost.Run(() => { PhotoTwins.Glass.Forced = before; PhotoTwins.Glass.Refresh(true); }); }
    }

    /// <summary>Shows the screen as it is while a scan runs (progress bar, Pause and Stop), without running one.</summary>
    internal static void Working(MainViewModel vm, bool on)
    {
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsScanning))!.SetValue(vm, on);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, on ? "Looking at photo 1,234 of 7,747…" : "");
    }

    private static List<Color> Stops(string key) => WpfHost.Run(() => Application.Current.Resources[key] switch
    {
        SolidColorBrush s => new List<Color> { s.Color },
        GradientBrush g => g.GradientStops.Select(x => x.Color).ToList(),
        _ => new List<Color>(),
    });

    private static T? Find<T>(DependencyObject d, Func<T, bool>? where = null) where T : DependencyObject
    {
        if (d is T t && (where == null || where(t))) return t;
        for (int k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++)
            if (Find(VisualTreeHelper.GetChild(d, k), where) is { } found) return found;
        return null;
    }

    private static bool Shown(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
        return true;
    }

    // ------------------------------------------------------------ the rule

    [Fact]
    public void Rule_the_glossy_look_is_used_everywhere_except_with_high_contrast()
    {
        Assert.True(PhotoTwins.Glass.SupportedFor(highContrast: false));
        Assert.False(PhotoTwins.Glass.SupportedFor(highContrast: true));
    }

    // ------------------------------------------------------------ the colors

    [Fact]
    public void Colors_a_dark_glass_header_over_a_long_soft_fade_from_charcoal_to_white_or_graphite_and_everything_else_is_neutral()
    {
        foreach (var dark in new[] { false, true })
        {
            WpfHost.SetTheme(dark);
            var band = Stops("HeroBrush");
            Assert.All(band, c => Assert.True(Lum(c) < 30 && Chroma(c) <= 6 && c.A >= 0xD8 && c.A < 0xFF, $"band {c}"));   // dark glass: nearly black, faintly see-through
            var page = Stops("PageBrush");                                                            // The user's pick (10-29): option A with background 1, "smoke"
            Assert.True(page.Count >= 3);
            Assert.All(page, c => Assert.True(Chroma(c) <= 8 && c.A == 0xFF, $"page {c}"));
            Assert.True(Lum(page[0]) < 40);                                                           // charcoal / near-black at the top ...
            if (dark) Assert.InRange(Lum(page[^1]), 30, 60);                                          // ... down to soft graphite
            else Assert.True(Lum(page[^1]) > 240);                                                    // ... down to white
            Assert.True(page.Select(Lum).Zip(page.Select(Lum).Skip(1)).All(p => p.Second >= p.First)); // always getting lighter
            foreach (var key in new[] { "GlassCardBrush", "GlassPhotoCardBrush", "GlassEdgeBrush", "ButtonFaceBrush", "ButtonEdgeBrush", "HeroButtonFaceBrush",
                                        "TabTrackBrush", "TabSelectedBrush", "GlassWindowBrush" })
                Assert.All(Stops(key), c => Assert.True(Chroma(c) <= 8, $"{key} {c} ({(dark ? "dark" : "light")})"));
            // the cards are clear glass (design note 10-30: "The foreground should be glass"): mostly see-through, so the fade shows through them
            Assert.All(Stops("GlassCardBrush"), c => Assert.InRange(c.A, 0x10, 0x70));
            Assert.All(Stops("GlassPhotoCardBrush"), c => Assert.InRange(c.A, 0x10, 0xA0));
            Assert.All(Stops("GlassSheenBrush"), c => Assert.True(c.R == 0xFF && c.G == 0xFF && c.B == 0xFF && c.A <= 0x80, $"sheen {c}"));
            // the buttons are glossy: lighter at the top than at the bottom
            foreach (var key in new[] { "ButtonFaceBrush", "HeroButtonFaceBrush", "AccentFaceBrush" })
            {
                var g = Stops(key);
                Assert.True(Lum(g[0]) > Lum(g[^1]), key);
            }
        }
        WpfHost.SetTheme(false);
    }

    [Fact]
    public void Colors_the_screens_hard_code_only_neutral_shades_and_the_one_progress_blue()
    {
        var src = System.IO.Path.Combine(Directory.GetParent(TestWorkspace.TestWorkRoot())!.FullName, "src", "PhotoTwins");
        var hex = new System.Text.RegularExpressions.Regex("#[0-9A-Fa-f]{6,8}\\b");
        var found = Directory.EnumerateFiles(src, "*.xaml").SelectMany(f => hex.Matches(File.ReadAllText(f)).Select(m => m.Value.ToUpperInvariant())).Distinct().ToList();
        Assert.NotEmpty(found);
        foreach (var h in found)
        {
            var c = (Color)ColorConverter.ConvertFromString(h);
            Assert.True(Chroma(c) == 0 || h == "#4C8DFF", $"{h} is a color hard-coded in a screen");
        }
        Assert.DoesNotContain("SystemFillColorCaution", string.Join("", Directory.EnumerateFiles(src, "*.xaml").Select(File.ReadAllText)));
    }

    // ------------------------------------------------------------ the styles follow light/dark and give way to high contrast

    [Fact]
    public void Styles_rounded_glossy_buttons_cards_and_tabs_are_in_place_follow_light_and_dark_and_give_way_to_high_contrast()
    {
        object[] keys = { typeof(Button), "AccentButtonStyle", "HeroAccentButton", "HeroButton", "LinkButton", "HeroLink", "GlassCard", "PillTabControl", "PillTab" };
        object[] windowsOwnWhenPlain = { typeof(Button), "AccentButtonStyle", "HeroAccentButton", "HeroButton", "PillTabControl", "PillTab" };
        // (the app's resources also hold Windows' own styles, so "ours" means the very style from Theme.xaml)
        bool Ours(object key) => WpfHost.Run(() => ReferenceEquals(Application.Current.TryFindResource(key), PhotoTwins.Glass.Theme[key]));
        Color FirstStop(string key) => Stops(key).First();
        bool HasSecondaryOverride() => WpfHost.Run(() => ((SolidColorBrush)Application.Current.TryFindResource("TextFillColorSecondaryBrush")).Color.A is 0xD9 or 0xE6);

        WpfHost.SetTheme(false);
        Assert.All(keys, k => Assert.True(Ours(k), k.ToString()));             // every button is the rounded glossy one, and so on
        Assert.True(HasSecondaryOverride());
        var lightCard = FirstStop("GlassCardBrush");
        WpfHost.SetTheme(true);                                                // Windows switches to dark while the app is open
        Assert.NotEqual(lightCard, FirstStop("GlassCardBrush"));
        Assert.True(FirstStop("GlassCardBrush").A < lightCard.A);              // fainter glass over the dark page
        WpfHost.SetTheme(false);

        With(false, () =>
        {
            Assert.All(windowsOwnWhenPlain, k => Assert.False(Ours(k), k.ToString()));   // Windows' own buttons and tabs ...
            Assert.All(windowsOwnWhenPlain, k => Assert.NotNull(WpfHost.Run(() => Application.Current.TryFindResource(k))));   // ... never the bare old-Windows style
            Assert.Same(WpfHost.Run(() => Application.Current.TryFindResource(typeof(TabControl))), WpfHost.Run(() => Application.Current.TryFindResource("PillTabControl")));
            Assert.False(HasSecondaryOverride());
            var plain = Stops("GlassWindowBrush").Single();
            Assert.Equal(255, plain.A);
            Assert.Equal(plain, Stops("HeroBrush").Single());                  // no black band either
            WpfHost.SetTheme(true);
            Assert.NotEqual(plain, Stops("GlassWindowBrush").Single());        // light and dark still differ
            WpfHost.SetTheme(false);
            return 0;
        });
        Assert.All(keys, k => Assert.True(Ours(k), k.ToString()));             // and the look comes back
        Assert.Equal(lightCard, FirstStop("GlassCardBrush"));
    }

    [Fact]
    public async Task Depth_buttons_have_a_soft_shadow_a_glossy_rounded_face_and_grain_and_cards_and_the_chosen_tab_float_on_a_shadow()
    {
        using var e = new Step3Env("gl-depth");
        e.Identical(1, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var problems = WpfHost.Run(() =>
        {
            var view = new MainView { DataContext = e.Vm };
            SelfCheck.Host(view, 1180, 780, 1.0);
            var scan = (Button)view.FindName("ScanButton");
            var remove = Find<Button>(view, b => b.Content is string s && s.StartsWith("Remove the others"));
            var r = new List<string>();
            if (remove == null) r.Add("no group button found");
            foreach (var b in new[] { scan, remove }.OfType<Button>())
            {
                if (Find<Border>(b, x => x.Effect is BlurEffect && x.Opacity > 0) == null) r.Add($"{b.Content}: no shadow");
                var face = Find<Border>(b, x => x.Background is LinearGradientBrush);
                if (face == null || face.CornerRadius.TopLeft < 16) r.Add($"{b.Content}: not a rounded glossy face");
                if (Find<System.Windows.Shapes.Rectangle>(b, x => x.Fill is ImageBrush) == null) r.Add($"{b.Content}: no grain");
            }
            var card = Find<ContentControl>(view, c => c.Content is StackPanel && c.Style == Application.Current.Resources["GlassCard"]);
            if (card == null || Find<Border>(card, x => x.Effect is BlurEffect) == null) r.Add("group card: no shadow");
            var tab = Find<TabItem>(view, t => t.IsSelected);
            if (tab == null || Find<Border>(tab, x => x.Effect is BlurEffect && x.Opacity > 0) == null) r.Add("chosen tab: not raised");
            return r;
        });
        Assert.Empty(problems);
    }

    // ------------------------------------------------------------ the buttons were rearranged

    [Fact]
    public async Task Layout_settings_and_scan_on_the_band_undo_and_remove_all_beside_the_tabs_and_pause_and_stop_only_while_something_runs()
    {
        using var e = new Step3Env("gl-layout");
        e.Identical(2, "a.jpg", "b.jpg");
        e.Identical(3, "S\\x.jpg", "S\\y.jpg");
        await e.ScanAsync();
        Assert.NotEmpty(e.Vm.ExactGroups);
        (bool onBand, bool undoOffBand, bool undoBesideTabs, bool pause, bool stop, bool exactAll, bool sameAll) Look() => WpfHost.Run(() =>
        {
            var view = new MainView { DataContext = e.Vm };
            SelfCheck.Host(view, 1280, 820, 1.0);
            bool OnBand(string name)
            {
                for (DependencyObject? d = (DependencyObject)view.FindName(name); d != null; d = VisualTreeHelper.GetParent(d))
                    if (d is Border b && ReferenceEquals(b.Background, Application.Current.Resources["HeroBrush"])) return true;
                return false;
            }
            // Undo sits on the same line as the tabs, at the right
            var undo = (Button)view.FindName("UndoButton");
            var track = Find<System.Windows.Controls.Primitives.TabPanel>(view)!;
            var u = undo.TransformToAncestor(view).TransformBounds(new Rect(undo.RenderSize));
            var t = track.TransformToAncestor(view).TransformBounds(new Rect(track.RenderSize));
            bool beside = u.Top < t.Bottom && u.Bottom > t.Top && u.Left > t.Right + 100;
            return (new[] { "ScanButton", "FoldersButton", "HeadlineText" }.All(OnBand), !OnBand("UndoButton"), beside,
                    Shown((DependencyObject)view.FindName("PauseButton")), Shown((DependencyObject)view.FindName("StopButton")),
                    Shown((DependencyObject)view.FindName("RemoveAllExactButton")), Shown((DependencyObject)view.FindName("RemoveAllSamePhotoButton")));
        });
        WpfHost.Run(() => e.Vm.SelectedTab = 0);
        var idle = Look();
        Assert.True(idle.onBand);
        Assert.True(idle.undoOffBand);
        Assert.True(idle.undoBesideTabs);                                       // the empty space beside the tabs now holds Undo ...
        Assert.True(idle.exactAll);                                             // ... and the open tab's "Remove all" button
        Assert.False(idle.sameAll);                                             // (the other tab's button stays hidden)
        Assert.False(idle.pause);                                               // nothing is running: no greyed-out Pause and Stop
        Assert.False(idle.stop);
        WpfHost.Run(() => e.Vm.SelectedTab = 1);
        var sameTab = Look();
        WpfHost.Run(() => e.Vm.SelectedTab = 0);
        Assert.False(sameTab.exactAll);
        Assert.Equal(e.Vm.RemoveAllSamePhotoVisibility == Visibility.Visible, sameTab.sameAll);
        WpfHost.Run(() => Working(e.Vm, true));
        var busy = Look();
        WpfHost.Run(() => Working(e.Vm, false));
        Assert.True(busy.pause);                                                // while a scan runs they are right beside the progress bar
        Assert.True(busy.stop);
    }

    [Fact]
    public void Rows_small_groups_sit_side_by_side_in_order_and_each_row_fills_the_width()
    {
        // needs: 2 photos = 590, 3 photos = 868, 6 photos = 1702
        Assert.Equal(590, GroupRows.Needs(2));
        var rows = GroupRows.Pack(new[] { 2, 2, 2, 3, 6, 2 }, 1250);
        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }, new[] { 5 } }, rows.Select(r => r.ToArray()).ToArray());
        rows = GroupRows.Pack(new[] { 2, 2, 2, 3, 2 }, 2000);
        Assert.Equal(new[] { new[] { 0, 1, 2 }, new[] { 3, 4 } }, rows.Select(r => r.ToArray()).ToArray());
        foreach (var counts in new[] { new[] { 2, 2 }, new[] { 2, 2, 2 }, new[] { 3, 2 } })
        {
            var w = GroupRows.Widths(counts, 2000);
            Assert.InRange(w.Sum() + GroupRows.Gap * (counts.Length - 1), 1997, 2000);   // the row ends at the right edge
            Assert.All(w.Zip(counts), p => Assert.True(p.First >= GroupRows.Needs(p.Second)));
        }
        Assert.Equal(new[] { 1200.0 }, GroupRows.Widths(new[] { 2 }, 1200));            // a group alone uses the full width
    }

    [Fact]
    public async Task Rows_on_a_wide_window_two_photo_groups_share_a_row_and_nothing_is_cut_off()
    {
        using var e = new Step3Env("gl-rows");
        for (int i = 0; i < 4; i++) e.Identical(40 + i, $"T{i}\\a.jpg", $"T{i}\\b.jpg");
        await e.ScanAsync();
        Assert.Equal(4, e.Vm.ExactGroups.Count);
        var (rowsWide, rowsNarrow, problems) = WpfHost.Run(() =>
        {
            int Rows(double width, List<string> p)
            {
                var view = new MainView { DataContext = e.Vm };
                SelfCheck.Host(view, width, 900, 1.0);
                p.AddRange(SelfCheck.FindCutOffText(view));
                return ((ItemsControl)view.FindName("ExactList")).Items.Count;
            }
            var problems = new List<string>();
            return (Rows(1640, problems), Rows(1080, problems), problems);
        });
        Assert.Equal(2, rowsWide);                                              // 4 groups of 2 photos: two per row on a wide window
        Assert.Equal(4, rowsNarrow);                                            // one per row when there's no room
        Assert.Empty(problems);
    }

    // ------------------------------------------------------------ the contrast check reads gradients where the words are

    [Fact]
    public void Contrast_check_judges_a_gradient_where_the_words_are()
    {
        var (top, bottom) = WpfHost.Run(() =>
        {
            List<string> Check(VerticalAlignment where)
            {
                var face = new Border
                {
                    Background = new LinearGradientBrush(Colors.Black, Colors.White, 90),
                    Child = new TextBlock { Text = "white words", Foreground = Brushes.White, VerticalAlignment = where, Margin = new Thickness(8) },
                };
                SelfCheck.Host(face, 300, 200, 1.0);
                return SelfCheck.FindLowContrastText(face, Colors.White);
            }
            return (Check(VerticalAlignment.Top), Check(VerticalAlignment.Bottom));
        });
        Assert.Empty(top);                                                     // white words on the black top: fine
        Assert.Single(bottom);                                                 // white words on the white bottom: caught
    }

    // ------------------------------------------------------------ every window and dialog has it

    [Fact]
    public async Task Windows_the_main_window_has_the_smoke_fade_and_every_dialog_the_soft_fade()
    {
        using var e = new Step3Env("gl-windows");
        e.Identical(1, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var cards = e.Vm.ExactGroups.Single().Cards;

        var windows = WpfHost.Run(() => new Window[]
        {
            new MainWindow(e.Vm),
            new UndoWindow(e.S),
            new ConfirmWindow(new ConfirmRequest("Photos in the cloud", "These photos are in OneDrive.", "Remove", null, "Cancel", DialogChoice.Cancel)),
            new FoldersWindow(e.S.Folders),
            new ViewerWindow(cards, 0, e.S),
            new ProblemsWindow(e.Vm.Result!, e.S.Log),
        });
        foreach (var w in windows)
        {
            var key = w is MainWindow ? "PageBrush" : "GlassWindowBrush";
            var expected = WpfHost.Run(() => (Brush)Application.Current.Resources[key]);
            Assert.Same(expected, WpfHost.Run(() => w.Background));
            WpfHost.Run(() => w.Close());
        }
    }

    [Fact]
    public void Windows_title_bar_is_black_for_the_main_window_matches_the_page_for_dialogs_and_is_Windows_own_with_high_contrast()
    {
        if (Environment.OSVersion.Version.Build < 22000) return;               // Windows 10 can't color a title bar: Windows' own is kept
        var (main, dialog, plain, result) = WpfHost.Run(() =>
        {
            uint Caption(bool hero)
            {
                var w = new Window();
                var hwnd = new WindowInteropHelper(w).EnsureHandle();          // a window handle without ever showing anything
                PhotoTwins.Glass.Frame(w, hero);
                int got = DwmGetWindowAttribute(hwnd, 35, out uint c, sizeof(uint));
                w.Close();
                return got == 0 ? c : uint.MaxValue;                           // (some Windows builds won't read the color back)
            }
            var m = Caption(true);
            var d = Caption(false);
            int r = PhotoTwins.Glass.LastFrameResult;
            PhotoTwins.Glass.Forced = false;
            var p = Caption(true);
            PhotoTwins.Glass.Forced = true;
            return (m, d, p, r);
        });
        uint Ref(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));
        Assert.Equal(0, result);                                               // Windows said yes (0 = accepted)
        if (main == uint.MaxValue) return;
        Assert.Equal(Ref(PhotoTwins.Glass.HeroTop), main);
        Assert.Equal(Ref(PhotoTwins.Glass.LightDialogTop), dialog);
        Assert.NotEqual(Ref(PhotoTwins.Glass.HeroTop), plain);                 // high contrast: Windows' own title bar
    }

    // ------------------------------------------------------------ photos are never covered or tinted

    private static Image? FirstPicture(DependencyObject d)
    {
        if (d is Image i && i.Source != null && i.ActualWidth > 50) return i;
        for (int k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++)
            if (FirstPicture(VisualTreeHelper.GetChild(d, k)) is { } found) return found;
        return null;
    }

    private static byte[] RenderPicturePixels(MainViewModel vm, Brush backdrop, double scale)
    {
        return WpfHost.Run(() =>
        {
            var view = new MainView { DataContext = vm };
            var host = SelfCheck.Host(view, 1180, 780, scale, backdrop);
            var rtb = new RenderTargetBitmap((int)(1180 * scale), (int)(780 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var img = FirstPicture(host) ?? throw new InvalidOperationException("no picture on screen");
            var r = img.TransformToAncestor(host).TransformBounds(new Rect(0, 0, img.ActualWidth, img.ActualHeight));
            int x = (int)((r.X + r.Width * 0.3) * scale), y = (int)((r.Y + r.Height * 0.3) * scale), w = (int)(r.Width * 0.4 * scale), h = (int)(r.Height * 0.4 * scale);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(new Int32Rect(x, y, w, h), px, w * 4, 0);
            return px;
        });
    }

    [Fact]
    public async Task Photos_are_drawn_exactly_the_same_over_black_over_white_and_over_a_busy_wallpaper_never_fogged_or_tinted()
    {
        using var e = new Step3Env("gl-photos");
        using (var scene = TestScenes.Scene(5))
        {
            var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, 92);
            e.Ws.Photo("a.jpg", bytes); e.Ws.Photo("b.jpg", bytes);
        }
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.ExactGroups);
        foreach (var dark in new[] { false, true })
        {
            WpfHost.SetTheme(dark);
            var over = new[] { Brushes.Black, Brushes.White, Wallpaper.Brush(7, blur: 14) }
                .Select(b => RenderPicturePixels(e.Vm, b, 1.25)).ToList();
            Assert.Equal(over[0], over[1]);
            Assert.Equal(over[0], over[2]);
            Assert.Contains(over[0], b => b != 0);                              // (and it really drew something)
        }
        WpfHost.SetTheme(false);
    }

    // ------------------------------------------------------------ readable on the worst wallpaper, in every state

    [Fact]
    public async Task Readable_main_screen_in_every_state_stays_easy_to_read_over_black_white_gray_and_the_strongest_colors()
    {
        using var e = new Step3Env("gl-readable");
        e.Identical(10, "a.jpg", "b.jpg", "c.jpg");
        e.Identical(11, "G\\x.jpg", "G\\y.jpg");
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.ExactGroups);
        ScreenShots.Take(e.Vm, "step5", "readable-results");                    // (these draw the screen light and dark, 100%, 125%, and 150%, and run the worst-wallpaper check)
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllExactAsync();
        ScreenShots.Take(e.Vm, "step5", "readable-after-removal");
        e.Vm.SelectedTab = 1;
        ScreenShots.Take(e.Vm, "step5", "readable-empty-tab");
        ScreenShots.TakeConfirm(new ConfirmRequest("Photos in the cloud", "These photos are in OneDrive. Removing them here also removes them from OneDrive online and from your other devices. Remove them?",
            "Remove", null, "Cancel", DialogChoice.Cancel), "readable-confirm");
        var undo = WpfHost.Run(() => new UndoViewModel(e.S, e.Vm));
        ScreenShots.TakeUndo(undo, "readable-undo");
    }

    // ------------------------------------------------------------ the plain fallback (high contrast)

    [Fact]
    public async Task Fallback_with_glass_off_the_screen_is_plain_solid_light_or_dark_and_text_is_readable()
    {
        using var e = new Step3Env("gl-plain");
        e.Identical(20, "a.jpg", "b.jpg");
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.ExactGroups);
        With(false, () =>
        {
            foreach (var dark in new[] { false, true })
                foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                {
                    WpfHost.SetTheme(dark);
                    var png = Path.Combine(ScreenShots.Folder("step5"), $"plain-main-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                    var problems = WpfHost.Run(() =>
                    {
                        var view = new MainView { DataContext = e.Vm };
                        SelfCheck.RenderToPng(view, 1180, 780, scale, png);
                        var p = SelfCheck.FindCutOffText(view);
                        p.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
                        // nothing in it is see-through: the fog layer is a solid color
                        var fog = (SolidColorBrush)Application.Current.Resources["GlassWindowBrush"];
                        if (fog.Color.A != 255) p.Add("the plain background is see-through");
                        return p;
                    });
                    Assert.True(File.Exists(png));
                    Assert.True(problems.Count == 0, string.Join(" | ", problems));
                }
            WpfHost.SetTheme(false);
            return 0;
        });
    }

    // ------------------------------------------------------------ the pictures for the user

    [Fact]
    public async Task Pictures_the_main_window_in_each_state_and_a_question_light_and_dark_at_100_and_125_percent()
    {
        using var e = new Step3Env("gl-pictures");
        var first = WpfHost.Run(() => new MainViewModel(e.S));                // before the first scan
        for (int i = 0; i < 4; i++)
        {
            using var scene = TestScenes.Scene(100 + i * 7);
            var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, 90);
            e.Ws.Photo($"Trip {i}\\IMG_{i:000}.jpg", bytes); e.Ws.Photo($"Trip {i}\\IMG_{i:000} (1).jpg", bytes);
            if (i % 2 == 0) e.Ws.Photo($"Downloads\\IMG_{i:000}.jpg", bytes);
        }
        for (int i = 0; i < 3; i++)
        {
            using var scene = TestScenes.Scene(300 + i * 5);
            e.Ws.Photo($"Phone\\PXL_{i:000}.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92));
            scene.Resize(1024, 768);
            e.Ws.Photo($"Chat\\PXL_{i:000}-small.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 80));
        }
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.ExactGroups, 4);
        ScreenShots.WaitForPictures(e.Vm.SameGroups, 3);

        ScreenShots.Take(first, "gloss", "first-run", width: 1280);
        ScreenShots.Take(e.Vm, "gloss", "main-window", width: 1280, height: 900);
        ScreenShots.Take(e.Vm, "gloss", "main-window-wide", width: 1700, height: 1000);
        WpfHost.Run(() => e.Vm.SelectedTab = 1);
        ScreenShots.Take(e.Vm, "gloss", "same-photo-tab", width: 1280, height: 900);
        WpfHost.Run(() => e.Vm.SelectedTab = 0);
        WpfHost.Run(() => Working(e.Vm, true));
        ScreenShots.Take(e.Vm, "gloss", "while-working", width: 1280);
        WpfHost.Run(() => Working(e.Vm, false));
        ScreenShots.TakeConfirm(new ConfirmRequest("Remove all exact copies?", "Remove 6 exact copies (24.3 MB)? Every group keeps one photo.", "Remove 6 photos", null, "Cancel", DialogChoice.Cancel,
            "Removed photos go to the Recycle Bin, or to a \"Duplicates to delete\" folder on drives that don't have one. You can undo it from the Undo list."), "question", step: "gloss");
        Assert.True(File.Exists(Path.Combine(ScreenShots.Folder("gloss"), "main-window-light-125.png")));
    }
}

/// <summary>A deliberately very busy made-up wallpaper, for the "nothing of the wallpaper shows, photos are never tinted" test.</summary>
internal static class Wallpaper
{
    public static Brush Brush(int seed, int blur)
    {
        var rnd = new Random(seed);
        using var img = TestScenes.Scene(seed, w: 1600, h: 1000);
        var d = new Drawables();
        for (int i = 0; i < 160; i++)
        {
            d.FillColor(new MagickColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            if (i % 2 == 0) d.Ellipse(rnd.Next(1600), rnd.Next(1000), rnd.Next(20, 160), rnd.Next(20, 160), 0, 360);
            else d.Rectangle(rnd.Next(1600), rnd.Next(1000), rnd.Next(1600), rnd.Next(1000));
        }
        d.Draw(img);
        if (blur > 0) img.Blur(0, blur);
        img.Format = MagickFormat.Png;
        var bytes = img.ToByteArray();
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
        var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }
}
