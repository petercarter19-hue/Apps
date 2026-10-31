using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ImageMagick;
using PhotoTwins.Tests.Support;
using K = PhotoTwins.Tests.Support.ShowcasePhotos.Kind;

namespace PhotoTwins.Tests;

/// <summary>
/// The showcase: a made-up photo library that looks real, used for the build-it-yourself kit's pictures and the video clips of the
/// app in use. It only runs when PHOTOTWINS_SHOWCASE names an output folder (it takes minutes), so the normal test run passes over it.
/// Like every other test it draws the app off-screen: the cursor in the clips is drawn into the picture, and the real screen, mouse
/// and keyboard are never touched. Removals use the real Recycle Bin on the test photos and are put back at the end.
/// </summary>
[Collection("WPF")]
public class ShowcaseTests
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("PHOTOTWINS_SHOWCASE");

    [Fact]
    public void Showcase_photos_preview()
    {
        if (string.IsNullOrEmpty(Out)) return;
        var dir = System.IO.Path.Combine(Out, "photos-preview");
        Directory.CreateDirectory(dir);
        foreach (var k in Enum.GetValues<K>())
        {
            using var img = ShowcasePhotos.Make(k, 1, w: 800, h: 600);
            img.Format = MagickFormat.Jpeg;
            img.Write(System.IO.Path.Combine(dir, k + ".jpg"));
        }
    }

    // ------------------------------------------------------------------ the library

    private static readonly Meta Phone = new(Make: "Apple", Model: "iPhone 15");

    /// <summary>A summer's worth of made-up phone photos, with the copies a real Pictures folder collects.</summary>
    internal static void MakeLibrary(TestWorkspace ws)
    {
        var originals = new (string name, K kind, int seed, string taken)[]
        {
            ("IMG_4101", K.SunsetLake, 1, "2025:07:12 20:41:09"),
            ("IMG_4102", K.Beach, 2, "2025:07:13 11:02:44"),
            ("IMG_4103", K.Beach, 3, "2025:07:13 11:20:13"),
            ("IMG_4104", K.GreenHills, 4, "2025:07:15 09:12:51"),
            ("IMG_4105", K.Lavender, 5, "2025:07:16 19:33:02"),
            ("IMG_4106", K.Dunes, 6, "2025:07:18 18:05:37"),
            ("IMG_4107", K.CityNight, 7, "2025:07:19 22:14:20"),
            ("IMG_4108", K.SnowPeaks, 8, "2025:07:21 08:47:05"),
            ("IMG_4109", K.AutumnHills, 9, "2025:07:22 16:30:41"),
            ("IMG_4110", K.MistyLake, 10, "2025:07:23 06:58:12"),
            ("IMG_4111", K.SunsetLake, 11, "2025:07:24 20:02:33"),
            ("IMG_4112", K.GreenHills, 12, "2025:07:25 13:44:09"),
            ("IMG_4113", K.CityNight, 13, "2025:07:26 23:01:54"),
            ("IMG_4116", K.Lavender, 16, "2025:07:28 19:10:26"),
            ("IMG_4117", K.Dunes, 17, "2025:07:29 07:22:48"),
        };
        var jpgs = new Dictionary<string, byte[]>();
        foreach (var o in originals)
        {
            using var img = ShowcasePhotos.Make(o.kind, o.seed);
            var bytes = TestScenes.Encode(img, MagickFormat.Jpeg, 90, Phone with { Taken = o.taken, SubSec = "120", Gps = true });
            jpgs[o.name] = bytes;
            Stamp(ws.Photo($"Phone import\\{o.name}.jpg", bytes), o.taken);
            if (o.name == "IMG_4101") ws.Photo("Sent in chat\\IMG-20250712-WA0004.jpg", TestScenes.ChatAppVersion(img));   // sent back in a chat app
            if (o.name == "IMG_4105") ws.Photo("Edited\\IMG_4105 small.jpg", TestScenes.Encode(img, MagickFormat.Jpeg, 85, Phone with { Taken = o.taken, SubSec = "120" }, percent: 50));
            if (o.name == "IMG_4107") ws.Photo("Converted\\IMG_4107.png", TestScenes.Encode(img, MagickFormat.Png, meta: Phone with { Taken = o.taken, SubSec = "120" }));
        }
        // the same file twice (or three times)
        ws.Photo("Desktop backup\\IMG_4101.jpg", jpgs["IMG_4101"]);
        ws.Photo("Beach trip\\IMG_4102.jpg", jpgs["IMG_4102"]);
        ws.Photo("Desktop backup\\IMG_4104.jpg", jpgs["IMG_4104"]);
        ws.Photo("Desktop backup\\IMG_4108.jpg", jpgs["IMG_4108"]);
        ws.Photo("Phone import\\IMG_4110 (1).jpg", jpgs["IMG_4110"]);
        ws.Photo("Desktop backup\\IMG_4112.jpg", jpgs["IMG_4112"]);
        ws.Photo("Beach trip\\IMG_4112 copy.jpg", jpgs["IMG_4112"]);
        // a burst: two shots of the northern lights a second apart (look-alikes, not copies)
        using (var a = ShowcasePhotos.Make(K.Aurora, 14))
            Stamp(ws.Photo("Phone import\\IMG_4114.jpg", TestScenes.Encode(a, MagickFormat.Jpeg, 90, Phone with { Taken = "2025:07:27 01:12:40", SubSec = "310" })), "2025:07:27 01:12:40");
        using (var b = ShowcasePhotos.Make(K.Aurora, 14, shift: 0.015))
            Stamp(ws.Photo("Phone import\\IMG_4115.jpg", TestScenes.Encode(b, MagickFormat.Jpeg, 90, Phone with { Taken = "2025:07:27 01:12:41", SubSec = "050" })), "2025:07:27 01:12:41");
    }

    /// <summary>Gives the original the file dates a phone import would have, so it is the oldest copy.</summary>
    private static void Stamp(string path, string taken)
    {
        var t = DateTime.ParseExact(taken, "yyyy:MM:dd HH:mm:ss", null).ToUniversalTime();
        File.SetCreationTimeUtc(path, t);
        File.SetLastWriteTimeUtc(path, t);
    }

    // ------------------------------------------------------------------ the pictures and the clips

    [Fact]
    public async Task Showcase_pictures_and_clips()
    {
        if (string.IsNullOrEmpty(Out)) return;
        var kit = System.IO.Path.Combine(Out, "kit");
        var clips = System.IO.Path.Combine(Out, "clips");
        Directory.CreateDirectory(kit);
        Directory.CreateDirectory(clips);

        var root = System.IO.Path.Combine(TestWorkspace.TestWorkRoot(), "showcase");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        using var ws = new TestWorkspace("showcase", exactName: true, profile: "Sam");
        MakeLibrary(ws);
        using var e = new Step3Env("showcase", realBin: true, existing: ws);
        e.Dialogs.Otherwise = DialogChoice.Primary;
        e.S.MaxParallelReads = 1;           // one photo at a time, so the progress bar can be seen moving
        var vm = e.Vm;
        WpfHost.SetTheme(false);
        var stage = WpfHost.Run(() => new Stage(vm));
        var cam = new Camera(stage);

        // 1. the first scan
        using (var film = new Film(System.IO.Path.Combine(clips, "01 first scan.mp4")))
        {
            cam.Still(film, 1.6);
            cam.SavePng(System.IO.Path.Combine(kit, "first-run.png"), cursor: false);
            cam.MoveTo(film, () => stage.Button("Scan"));
            cam.Click(film);
            string? mid = System.IO.Path.Combine(kit, "scanning.png");
            ws.Fs.BeforeOpen = _ => Thread.Sleep(110);   // a small library scans in a blink; slowed so the progress bar can be seen
            await cam.WhileRunning(film, WpfHost.Run(() => vm.StartScanAsync()), atHalf: mid);
            ws.Fs.BeforeOpen = null;
            await cam.UntilPicturesShow(film, () => vm.ExactGroups);
            cam.MoveTo(film, new Point(1180, 520), 0.8);
            cam.Still(film, 2.5);
        }
        cam.SavePng(System.IO.Path.Combine(kit, "results.png"), cursor: false);

        // 2. looking through the groups
        using (var film = new Film(System.IO.Path.Combine(clips, "02 browse the groups.mp4")))
        {
            cam.Still(film, 0.8);
            cam.Scroll(film, 0, 520, 3.0);
            cam.Still(film, 0.8);
            cam.Scroll(film, 520, 0, 1.6);
            cam.MoveTo(film, () => stage.Tab(1));
            cam.Click(film, () => vm.SelectedTab = 1);
            await cam.UntilPicturesShow(film, () => vm.SameGroups);
            cam.Still(film, 1.5);
            cam.SavePng(System.IO.Path.Combine(kit, "same-photo.png"), cursor: false);
            cam.Scroll(film, 0, 640, 3.4);
            cam.Still(film, 1.2);
            cam.Scroll(film, 640, 0, 1.4);
        }

        // 3. a photo at full size
        using (var film = new Film(System.IO.Path.Combine(clips, "03 full size.mp4")))
        {
            var group = vm.SameGroups[0];
            cam.MoveTo(film, () => stage.Picture(group.Cards[0]));
            var viewer = WpfHost.Run(() => new ViewerViewModel(group.Cards, 0, e.S));
            cam.Click(film);
            await WpfHost.Run(() => viewer.LoadCurrentAsync());
            WpfHost.Run(() => stage.ShowViewer(viewer));
            cam.Still(film, 1.8);
            cam.SavePng(System.IO.Path.Combine(kit, "viewer.png"), cursor: false);
            for (int i = 0; i < 2; i++)
            {
                cam.MoveTo(film, () => stage.Button("Next"), 0.5);
                cam.Click(film);
                await WpfHost.Run(() => viewer.GoAsync(1));
                cam.Still(film, 1.4);
            }
            cam.MoveTo(film, () => stage.Button("Close"), 0.5);
            cam.Click(film, () => stage.CloseOverlay());
            cam.Still(film, 1.0);
        }

        // 4. the question before a bulk removal (answered Cancel: nothing removed)
        WpfHost.Run(() => vm.SelectedTab = 0);
        await cam.UntilPicturesShow(null, () => vm.ExactGroups);
        using (var film = new Film(System.IO.Path.Combine(clips, "04 remove all asks first.mp4")))
        {
            cam.Still(film, 0.6);
            e.Dialogs.Answer(DialogChoice.Cancel);
            cam.MoveTo(film, () => stage.Button("Remove all exact copies"));
            cam.Click(film);
            await WpfHost.Run(() => vm.RemoveAllExactAsync());
            var q = e.Dialogs.Requests[^1];
            WpfHost.Run(() => stage.ShowQuestion(q));
            cam.Still(film, 2.6);
            cam.SavePng(System.IO.Path.Combine(kit, "remove-all-question-in-app.png"), cursor: false);
            cam.MoveTo(film, () => stage.Button(q.CancelText), 0.6);
            cam.Click(film, () => stage.CloseOverlay());
            cam.Still(film, 1.0);
            WpfHost.Run(() => SavePng(stage.QuestionAlone(q), System.IO.Path.Combine(kit, "remove-all-question.png")));
        }

        // 5. moving the star, then removing the others in one group
        using (var film = new Film(System.IO.Path.Combine(clips, "05 keeper and remove.mp4")))
        {
            var g = vm.ExactGroups[0];
            cam.Still(film, 0.6);
            cam.MoveTo(film, () => stage.Button("Make this the keeper", g));
            cam.Click(film, () => g.Cards[1].MakeKeeperCommand.Execute(null));
            cam.Still(film, 1.4);
            cam.SavePng(System.IO.Path.Combine(kit, "keeper-moved.png"), cursor: false);
            cam.MoveTo(film, () => stage.Button("Remove the others", g));
            cam.Click(film);
            await cam.WhileRunning(film, WpfHost.Run(() => vm.RemoveGroupAsync(g)));
            cam.MoveTo(film, new Point(760, 60), 0.9);
            cam.Still(film, 2.4);
            cam.SavePng(System.IO.Path.Combine(kit, "after-removal.png"), cursor: false);
        }

        // 6. Undo puts it back, in the same place
        using (var film = new Film(System.IO.Path.Combine(clips, "06 undo puts it back.mp4")))
        {
            cam.Still(film, 0.5);
            var undo = WpfHost.Run(() => new UndoViewModel(e.S, vm));
            cam.MoveTo(film, () => stage.Button("Undo"));
            cam.Click(film, () => stage.ShowUndo(undo));
            cam.Still(film, 1.8);
            WpfHost.Run(() => SavePng(stage.UndoAlone(undo), System.IO.Path.Combine(kit, "undo.png")));
            cam.MoveTo(film, () => stage.Button("Put back"), 0.7);
            cam.Click(film);
            await cam.WhileRunning(film, WpfHost.Run(() => undo.PutBackAsync(undo.Actions[0].AllRows[0].Entry)));
            cam.Still(film, 1.4);
            cam.MoveTo(film, () => stage.Button("Close"), 0.6);
            cam.Click(film, () => stage.CloseOverlay());
            await cam.UntilPicturesShow(film, () => vm.ExactGroups);
            cam.Still(film, 2.0);
        }

        // 7. look-alikes: switched on in Settings, then scanned again
        using (var film = new Film(System.IO.Path.Combine(clips, "07 look-alikes.mp4")))
        {
            cam.Still(film, 0.5);
            cam.MoveTo(film, () => stage.Button("Settings"));
            FoldersWindow? settings = null;
            cam.Click(film, () => settings = stage.ShowSettings(e.S.Folders));
            cam.Still(film, 1.4);
            cam.MoveTo(film, () => stage.Named<CheckBox>("LookAlikesCheck"), 0.8);
            cam.Click(film, () => settings!.LookAlikesCheck.IsChecked = true);
            cam.Still(film, 1.2);
            WpfHost.Run(() => SavePng(stage.SettingsAlone(e.S.Folders), System.IO.Path.Combine(kit, "settings.png")));
            cam.MoveTo(film, () => stage.Button("Done"), 0.7);
            cam.Click(film, () => { stage.CloseOverlay(); vm.RefreshFolders(); });
            cam.Still(film, 0.8);
            cam.MoveTo(film, () => stage.Button("Scan"));
            cam.Click(film);
            await cam.WhileRunning(film, WpfHost.Run(() => vm.StartScanAsync()));
            await cam.UntilPicturesShow(film, () => vm.ExactGroups);
            cam.MoveTo(film, () => stage.Tab(2));
            cam.Click(film, () => vm.SelectedTab = 2);
            await cam.UntilPicturesShow(film, () => vm.LookAlikeGroups);
            cam.Still(film, 2.6);
            cam.SavePng(System.IO.Path.Combine(kit, "look-alikes.png"), cursor: false);
        }

        // 8. the same app in dark mode
        WpfHost.Run(() => vm.SelectedTab = 0);
        WpfHost.SetTheme(true);
        stage.Retheme();
        await cam.UntilPicturesShow(null, () => vm.ExactGroups);
        using (var film = new Film(System.IO.Path.Combine(clips, "08 dark mode.mp4")))
        {
            cam.MoveTo(film, new Point(1180, 560), 0.1);
            cam.Still(film, 1.4);
            cam.SavePng(System.IO.Path.Combine(kit, "dark.png"), cursor: false);
            cam.Scroll(film, 0, 520, 3.0);
            cam.Still(film, 1.0);
            cam.MoveTo(film, () => stage.Tab(1));
            cam.Click(film, () => vm.SelectedTab = 1);
            await cam.UntilPicturesShow(film, () => vm.SameGroups);
            cam.Still(film, 2.0);
        }
        WpfHost.SetTheme(false);
        stage.Retheme();

        // and all of it as one tour
        var parts = Directory.GetFiles(clips, "0*.mp4").Where(p => !System.IO.Path.GetFileName(p).StartsWith("00")).OrderBy(p => p).ToList();
        var list = System.IO.Path.Combine(clips, "parts.txt");
        File.WriteAllLines(list, parts.Select(p => $"file '{p.Replace("\\", "/")}'"));
        Film.Ffmpeg($"-loglevel error -y -f concat -safe 0 -i \"{list}\" -c copy \"{System.IO.Path.Combine(clips, "00 app tour.mp4")}\"");
        File.Delete(list);
        Assert.True(parts.Count == 8 && parts.All(p => new FileInfo(p).Length > 50_000));
    }

    internal static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }
}

/// <summary>Writes video frames straight into ffmpeg (1920 × 1080, 30 a second).</summary>
internal sealed class Film : IDisposable
{
    public const int Fps = 30;
    private static readonly string Exe = Environment.GetEnvironmentVariable("PHOTOTWINS_FFMPEG") ?? "ffmpeg";   // the free video tool: its full path, or on the PATH
    private readonly Process _p;
    private readonly System.Text.StringBuilder _err = new();
    private byte[]? _last;

    public Film(string mp4)
    {
        var psi = new ProcessStartInfo(Exe, $"-loglevel error -y -f rawvideo -pix_fmt bgra -s {Stage.Pw}x{Stage.Ph} -r {Fps} -i - " +
                                            $"-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -movflags +faststart \"{mp4}\"")
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        _p = Process.Start(psi)!;
        _p.ErrorDataReceived += (_, a) => { if (a.Data != null) lock (_err) _err.AppendLine(a.Data); };
        _p.BeginErrorReadLine();
    }

    public void Add(byte[] frame, int times = 1)
    {
        for (int i = 0; i < times; i++) _p.StandardInput.BaseStream.Write(frame);
        _last = frame;
    }

    public void Hold(double seconds) { if (_last != null) Add(_last, (int)Math.Round(seconds * Fps)); }

    public static void Ffmpeg(string args)
    {
        var psi = new ProcessStartInfo(Exe, args) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + err);
    }

    public void Dispose()
    {
        _p.StandardInput.Close();
        _p.WaitForExit();
        if (_p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + _err);
        _p.Dispose();
    }
}

/// <summary>Moves the drawn cursor, clicks, scrolls and films the stage. Every frame is made on the WPF thread, off-screen.</summary>
internal sealed class Camera
{
    private readonly Stage _s;
    public Camera(Stage s) { _s = s; }

    private byte[] Shot() => WpfHost.Run(() => _s.Shot(true));

    public void Still(Film f, double seconds) { f.Add(Shot()); f.Hold(seconds - 1.0 / Film.Fps); }

    public void SavePng(string path, bool cursor) => WpfHost.Run(() => ShowcaseTests.SavePng(_s.Bitmap(cursor), path));

    public void MoveTo(Film f, Func<FrameworkElement> target, double seconds = 0.7) =>
        MoveTo(f, WpfHost.Run(() => _s.CenterOf(target())), seconds);

    public void MoveTo(Film f, Point to, double seconds = 0.7)
    {
        var from = WpfHost.Run(() => _s.Cursor);
        int n = Math.Max(1, (int)(seconds * Film.Fps));
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n;
            t = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;    // ease in and out
            var p = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
            f.Add(WpfHost.Run(() => { _s.Cursor = p; return _s.Shot(true); }));
        }
    }

    /// <summary>A press: the cursor dips and a soft ring spreads; then the action happens.</summary>
    public void Click(Film f, Action? then = null)
    {
        for (int i = 0; i < 7; i++)
        {
            int k = i;
            f.Add(WpfHost.Run(() => { _s.Press(k / 6.0); return _s.Shot(true); }));
        }
        WpfHost.Run(() => { _s.Press(null); then?.Invoke(); });
    }

    /// <summary>Films the screen while something runs, at the speed it really runs (at least half a second).</summary>
    public async Task WhileRunning(Film f, Task running, string? atHalf = null)
    {
        var clock = Stopwatch.StartNew();
        int written = 0;
        bool savedHalf = false;
        while (!running.IsCompleted || clock.Elapsed.TotalSeconds < 0.5)
        {
            var frame = Shot();
            int due = (int)(clock.Elapsed.TotalSeconds * Film.Fps) - written;
            f.Add(frame, Math.Max(1, due));
            written += Math.Max(1, due);
            if (atHalf != null && !savedHalf && WpfHost.Run(() => _s.ProgressAbout(0.45)))
            {
                SavePng(atHalf, cursor: false);
                savedHalf = true;
            }
        }
        await running;
    }

    /// <summary>Films until the pictures of the first groups are drawn (they appear one by one, as in the real app).</summary>
    public async Task UntilPicturesShow(Film? f, Func<IReadOnlyList<GroupVm>> groups)
    {
        var cards = WpfHost.Run(() => groups().Take(6).SelectMany(g => g.Cards).ToList());
        var clock = Stopwatch.StartNew();
        int written = 0;
        while (clock.Elapsed.TotalSeconds < 30)
        {
            bool all = WpfHost.Run(() => cards.All(c => c.Picture != null || c.PictureStatus.StartsWith("Couldn't")));
            if (f != null)
            {
                var frame = Shot();
                int due = Math.Max(1, (int)(clock.Elapsed.TotalSeconds * Film.Fps) - written);
                f.Add(frame, due);
                written += due;
            }
            if (all) return;
            await Task.Delay(f == null ? 50 : 1);
        }
        throw new TimeoutException("pictures were not drawn in time");
    }

    public void Scroll(Film f, double from, double to, double seconds)
    {
        int n = (int)(seconds * Film.Fps);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n;
            t = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            double y = from + (to - from) * t;
            f.Add(WpfHost.Run(() => { _s.ScrollTo(y); return _s.Shot(true); }));
        }
    }
}

/// <summary>The app's main screen at 125% (1920 × 1080 pixels), with room on top for a dialog and a drawn cursor.</summary>
internal sealed class Stage
{
    public const double Wd = 1536, Hd = 864, Scale = 1.25;
    public const int Pw = 1920, Ph = 1080;

    private readonly Grid _host = new() { Width = Wd, Height = Hd };
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Canvas _pointer = new() { IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _arrow;
    private readonly Ellipse _ring = new() { Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), StrokeThickness = 3, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _press = new(1, 1);
    private readonly MainViewModel _vm;
    public MainView View { get; }
    public Point Cursor { get; set; } = new(1180, 620);

    public Stage(MainViewModel vm)
    {
        _vm = vm;
        View = new MainView { DataContext = vm };
        _host.SetResourceReference(Panel.BackgroundProperty, "PageBrush");
        _host.Children.Add(View);
        _host.Children.Add(_overlay);
        _arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L0,17 L4.6,12.8 L7.6,19.6 L10.4,18.4 L7.4,11.8 L13,11.8 Z"),
            Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.1, StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.35, 1.35), _press } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = 0.45 },
        };
        _pointer.Children.Add(_ring);
        _pointer.Children.Add(_arrow);
        _host.Children.Add(_pointer);
        VisualTreeHelper.SetRootDpi(_host, new DpiScale(Scale, Scale));
    }

    /// <summary>
    /// Picks up a switch between light and dark. The stage is not in a window, so WPF never tells it the colors changed; putting the screen
    /// back in place makes it look its colors up again. Then checks the page really is dark (or light) now.
    /// </summary>
    public void Retheme() => WpfHost.Run(() =>
    {
        _host.Children.Remove(View);
        _host.Children.Insert(0, View);
        _host.SetResourceReference(Panel.BackgroundProperty, "PageBrush");
        WpfHost.DoEvents();
        var bottom = ((GradientBrush)_host.Background).GradientStops[^1].Color;
        bool dark = 0.299 * bottom.R + 0.587 * bottom.G + 0.114 * bottom.B < 128;
        if (dark != PhotoTwins.Glass.IsDark) throw new InvalidOperationException($"the stage did not switch to {(PhotoTwins.Glass.IsDark ? "dark" : "light")}");
    });

    private void Layout()
    {
        WpfHost.DoEvents();
        _host.Measure(new Size(Wd, Hd));
        _host.Arrange(new Rect(0, 0, Wd, Hd));
        _host.UpdateLayout();
    }

    public BitmapSource Bitmap(bool cursor)
    {
        _pointer.Visibility = cursor ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_arrow, Cursor.X); Canvas.SetTop(_arrow, Cursor.Y);
        Layout();
        var rtb = new RenderTargetBitmap(Pw, Ph, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(_host);
        rtb.Freeze();
        _pointer.Visibility = Visibility.Visible;
        return rtb;
    }

    public byte[] Shot(bool cursor)
    {
        var bmp = Bitmap(cursor);
        var buf = new byte[Pw * Ph * 4];
        bmp.CopyPixels(buf, Pw * 4, 0);
        return buf;
    }

    /// <summary>Null = released. Otherwise how far through the press (0 to 1).</summary>
    public void Press(double? t)
    {
        if (t == null) { _ring.Visibility = Visibility.Collapsed; _press.ScaleX = _press.ScaleY = 1; return; }
        double r = 6 + 22 * t.Value;
        _ring.Width = _ring.Height = r * 2;
        Canvas.SetLeft(_ring, Cursor.X - r); Canvas.SetTop(_ring, Cursor.Y - r);
        _ring.Opacity = 1 - t.Value;
        _ring.Visibility = Visibility.Visible;
        _press.ScaleX = _press.ScaleY = t.Value < 0.5 ? 0.88 : 1;
    }

    public bool ProgressAbout(double at) => View.ScanProgressBar.Maximum > 0 && View.ScanProgressBar.Value / View.ScanProgressBar.Maximum >= at;

    public Point CenterOf(FrameworkElement el)
    {
        Layout();
        var p = el.TransformToAncestor(_host).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        return new Point(p.X - 2, p.Y - 2);
    }

    // ---- finding things on screen

    private DependencyObject Searched => _overlay.Visibility == Visibility.Visible ? _overlay : View;

    private static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is UIElement u && u.Visibility != Visibility.Visible) continue;
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    private static string TextOf(ButtonBase b) => b.Content as string ?? All(b).OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

    /// <summary>The first shown button whose words contain this (inside a group's card, if one is given).</summary>
    public FrameworkElement Button(string words, GroupVm? inGroup = null)
    {
        Layout();
        DependencyObject root = Searched;
        if (inGroup != null) root = All(View).OfType<FrameworkElement>().First(f => ReferenceEquals(f.DataContext, inGroup));
        var buttons = All(root).OfType<ButtonBase>().ToList();
        return buttons.FirstOrDefault(b => b.ActualWidth > 0 && TextOf(b).Contains(words, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No button \"{words}\" on screen. Shown: " + string.Join(" | ", buttons.Select(b => $"{TextOf(b)} ({b.ActualWidth:0})")));
    }

    public T Named<T>(string name) where T : FrameworkElement { Layout(); return All(Searched).OfType<T>().First(x => x.Name == name); }

    public FrameworkElement Tab(int index) { Layout(); return All(View).OfType<TabItem>().Where(t => t.ActualWidth > 0).ElementAt(index); }

    public FrameworkElement Picture(CardVm card)
    {
        Layout();
        var holder = All(View).OfType<FrameworkElement>().First(f => ReferenceEquals(f.DataContext, card) && f.ActualWidth > 0);
        return All(holder).OfType<Image>().First();
    }

    public void ScrollTo(double y)
    {
        var list = _vm.SelectedTab switch { 0 => View.ExactList, 1 => View.SameList, _ => View.LookAlikeList };
        Layout();
        All(list).OfType<ScrollViewer>().First().ScrollToVerticalOffset(y);
    }

    // ---- dialogs, drawn over the dimmed main screen the way the real windows sit over it

    private void Show(FrameworkElement content, double w, double h)
    {
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) });
        var card = new Border
        {
            Width = w, Height = double.IsNaN(h) ? double.NaN : h, CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.45 },
            Child = content, ClipToBounds = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, "GlassWindowBrush");
        _overlay.Children.Add(card);
        _overlay.Visibility = Visibility.Visible;
    }

    public void CloseOverlay() { _overlay.Children.Clear(); _overlay.Visibility = Visibility.Collapsed; }

    public void ShowViewer(ViewerViewModel v) => Show(new ViewerView { DataContext = v }, 1180, 800);

    public void ShowUndo(UndoViewModel u) => Show(new UndoView { DataContext = u }, 940, 640);

    public void ShowQuestion(ConfirmRequest q) => Show(new ConfirmView(q), 640, double.NaN);

    public FoldersWindow ShowSettings(PhotoTwins.Core.Settings.FolderList folders)
    {
        var w = new FoldersWindow(folders);
        var content = (FrameworkElement)w.Content;
        w.Content = null;                       // the window itself is never shown; only its contents are drawn
        var holder = new Border { Child = content };
        holder.SetResourceReference(Border.BackgroundProperty, "GlassWindowBrush");
        Show(holder, 780, 840);
        _settings = w;
        return w;
    }
    private FoldersWindow? _settings;

    // ---- the same dialogs on their own, for the kit

    private static BitmapSource Alone(FrameworkElement view, double w, double h)
    {
        var png = System.IO.Path.GetTempFileName();
        try
        {
            SelfCheck.RenderToPng(view, w, h, Scale, png);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(png); bmp.EndInit(); bmp.Freeze();
            return bmp;
        }
        finally { File.Delete(png); }
    }

    public BitmapSource QuestionAlone(ConfirmRequest q)
    {
        var v = new ConfirmView(q);
        v.Measure(new Size(640, double.PositiveInfinity));
        return Alone(v, 640, Math.Ceiling(v.DesiredSize.Height));
    }

    public BitmapSource UndoAlone(UndoViewModel u) => Alone(new UndoView { DataContext = u }, 940, 640);

    public BitmapSource SettingsAlone(PhotoTwins.Core.Settings.FolderList folders)
    {
        var w = new FoldersWindow(folders);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        content.Margin = new Thickness(0);
        return Alone(new Border { Child = content }, 780, 840);
    }
}
