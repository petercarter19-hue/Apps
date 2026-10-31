using System.Diagnostics;
using ImageMagick;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// How long Pete waits for the pictures in the first groups to appear, measured on a folder of 2,000 made-up photos (JPEG and HEIC).
/// Not part of the normal run (the folder takes a few minutes to make the first time): set PHOTOTWINS_BENCH=label to run it.
/// The folder is kept in test-work\bench and reused. Results are written to test-work\bench\result-label.txt.
/// </summary>
[Collection("WPF")]
public class PictureSpeedBench
{
    private const int JpegSets = 634, HeicSets = 49;   // 634 x 3 + 49 x 2 = 2,000 photos

    public static string BenchRoot() => Path.Combine(TestWorkspace.TestWorkRoot(), "bench");

    /// <summary>Makes the folder once: phone-sized (6-megapixel) JPEGs in threes and HEIC photos in pairs, spread over month folders.</summary>
    public static string EnsureFolder()
    {
        var pics = Path.Combine(BenchRoot(), "Pictures");
        var marker = Path.Combine(BenchRoot(), "ready.txt");
        if (File.Exists(marker)) return pics;
        Directory.CreateDirectory(pics);
        var heicSrc = Path.Combine(BenchRoot(), "heic-src");
        Directory.CreateDirectory(heicSrc);

        Parallel.For(0, JpegSets, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            using var img = TestScenes.Scene(1000 + i);                       // 640x480 made-up scene ...
            img.Resize(new MagickGeometry(3000, 2250) { IgnoreAspectRatio = true });   // ... blown up to phone size
            img.AddNoise(NoiseType.Gaussian, 0.4);                              // real photos are never smooth
            img.Quality = 88; img.Format = MagickFormat.Jpeg;
            var bytes = img.ToByteArray();
            var month = $"2026-{1 + i % 10:00}";
            File.WriteAllBytes(Path.Combine(pics, month, $"IMG_{i:0000}.jpg").EnsureDir(), bytes);
            File.WriteAllBytes(Path.Combine(pics, month, $"IMG_{i:0000} (1).jpg").EnsureDir(), bytes);
            File.WriteAllBytes(Path.Combine(pics, "Downloads", $"IMG_{i:0000}.jpg").EnsureDir(), bytes);
        });
        Parallel.For(0, HeicSrcCount(), new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            using var img = TestScenes.Scene(5000 + i);
            img.Resize(new MagickGeometry(3000, 2250) { IgnoreAspectRatio = true });
            img.AddNoise(NoiseType.Gaussian, 0.4);
            img.Format = MagickFormat.Png;
            File.WriteAllBytes(Path.Combine(heicSrc, $"h{i:000}.png"), img.ToByteArray());
        });
        var py = new ProcessStartInfo("python", $"\"{Path.Combine(AppRoot(), "tools", "make-bench-heic.py")}\" \"{heicSrc}\" \"{Path.Combine(pics, "iPhone")}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(py)!)
        {
            var err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("HEIC maker failed: " + err);
        }
        File.WriteAllText(marker, "ok");
        return pics;
    }

    private static int HeicSrcCount() => HeicSets;
    private static string AppRoot() => Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;

    private sealed class BenchEnv : IDisposable
    {
        public AppServices S { get; }
        public MainViewModel Vm { get; }
        public BenchEnv(string appData, string pictures)
        {
            var fs = new GuardedFileSystem(BenchRoot());
            var policy = new ScanPolicy(Path.Combine(BenchRoot(), "me"), Array.Empty<string>(), _ => false);
            S = new AppServices(new AppPaths(appData), fs, policy, pictures, null, new AppServicesOptions { Volumes = new NoBinRules() });
            Vm = WpfHost.Run(() => new MainViewModel(S, new FakeDialogs()));
        }
        public void Dispose() => S.Dispose();
    }

    /// <summary>How long until every picture in these cards is drawn (as the screen would ask for them).</summary>
    private static double SecondsUntilDrawn(IEnumerable<CardVm> cards, int timeoutSeconds = 600)
    {
        var list = cards.ToList();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            bool all = WpfHost.Run(() => list.All(c => c.Picture != null || c.PictureStatus.StartsWith("Couldn't")));
            if (all) return clock.Elapsed.TotalSeconds;
            Thread.Sleep(10);
        }
        throw new TimeoutException("pictures were not drawn in time");
    }

    [Fact]
    public async Task Bench_how_fast_the_first_pictures_appear()
    {
        var label = Environment.GetEnvironmentVariable("PHOTOTWINS_BENCH");
        if (string.IsNullOrWhiteSpace(label)) return;   // not part of the normal run

        var pics = EnsureFolder();
        var appData = Path.Combine(BenchRoot(), "appdata-" + label + "-" + DateTime.Now.ToString("HHmmss"));
        var lines = new List<string> { $"Bench {label}, {DateTime.Now:yyyy-MM-dd HH:mm}, photos: {Directory.EnumerateFiles(pics, "*", SearchOption.AllDirectories).Count()}" };

        using (var env = new BenchEnv(appData, pics))
        {
            var scan = Stopwatch.StartNew();
            await WpfHost.Run(() => env.Vm.StartScanAsync());
            lines.Add($"Scan (first time, nothing remembered): {scan.Elapsed.TotalSeconds:0.0} s, {env.Vm.ExactGroups.Count} exact groups");

            var first6 = env.Vm.ExactGroups.Take(6).SelectMany(g => g.Cards).ToList();
            lines.Add($"First screen holds {first6.Count(c => c.FullPath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase))} HEIC and {first6.Count(c => c.FullPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))} JPEG pictures");
            lines.Add($"First screen (first 6 groups, {first6.Count} pictures) drawn {SecondsUntilDrawn(first6):0.00} s after the groups appear");

            var heicCards = env.Vm.ExactGroups.Where(g => g.Cards.Any(c => c.FullPath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase))).Take(6).SelectMany(g => g.Cards).ToList();
            lines.Add($"First 6 groups that hold HEIC photos ({heicCards.Count} pictures) drawn {SecondsUntilDrawn(heicCards):0.00} s after the groups appear");

            var first24 = env.Vm.ExactGroups.Take(24).SelectMany(g => g.Cards).Where(c => !first6.Contains(c)).ToList();
            lines.Add($"The next 18 groups ({first24.Count} more pictures) drawn {SecondsUntilDrawn(first24):0.00} s later");
        }

        // closing and opening the app again: everything is remembered
        using (var env = new BenchEnv(appData, pics))
        {
            var scan = Stopwatch.StartNew();
            await WpfHost.Run(() => env.Vm.StartScanAsync());
            lines.Add($"Second scan (everything remembered): {scan.Elapsed.TotalSeconds:0.0} s");
            var first6 = env.Vm.ExactGroups.Take(6).SelectMany(g => g.Cards).ToList();
            lines.Add($"Second time: first screen drawn {SecondsUntilDrawn(first6):0.00} s after the groups appear");
        }

        long bytes = Directory.EnumerateFiles(appData, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        lines.Add($"App data folder size: {bytes / 1024.0 / 1024.0:0.0} MB");
        foreach (var f in Directory.EnumerateFiles(appData, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length > 100_000))
            lines.Add($"   {Path.GetRelativePath(appData, f)}: {new FileInfo(f).Length / 1024.0 / 1024.0:0.0} MB");
        File.WriteAllLines(Path.Combine(BenchRoot(), $"result-{label}.txt"), lines);
    }
}

internal static class PathExt
{
    public static string EnsureDir(this string file) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); return file; }
}
