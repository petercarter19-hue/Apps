using ImageMagick;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

[Collection("WPF")]
public class ThumbnailNotificationTests
{
    [Fact]
    public void A_picture_failure_is_not_published_until_its_log_write_finishes()
    {
        using var ws = new TestWorkspace("thumbnail-log-order");
        var path = ws.Photo("photo.jpg", new byte[] { 1, 2, 3 });
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Thread? worker = null;
        ws.Fs.BeforeOpen = _ => { worker = Thread.CurrentThread; entered.Set(); throw new IOException("synthetic read failure"); };
        using var service = new ThumbnailService(ws.Fs, ws.Log);
        var logGate = typeof(PhotoTwins.Core.Logging.AppLog).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ws.Log)!;

        lock (logGate)
        {
            service.Request("test-key", path, () => finished.Set());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() => worker != null && (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            Assert.Null(service.FailureOf("test-key", path));
        }
        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(service.FailureOf("test-key", path));
        Assert.Contains("synthetic read failure", File.ReadAllText(ws.Log.CurrentFile));
    }

    [Fact]
    public async Task An_edited_cold_photo_does_not_replace_the_picture_of_its_unchanged_copy()
    {
        using var e = new Step3Env("thumbnail-edited-pair");
        for (int i = 0; i < 12; i++)
        {
            using var scene = TestScenes.Scene(1000 + i, w: 96, h: 72);
            var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, 90);
            e.Ws.Photo($"G{i:00}\\a.jpg", bytes); e.Ws.Photo($"G{i:00}\\b.jpg", bytes);
        }
        await e.ScanAsync();
        var cards = e.Vm.ExactGroups[11].Cards;
        var key = cards[0].ThumbnailKey;
        e.S.ThumbStore.Remove(new[] { key });
        using var edited = TestScenes.Scene(1999, w: 96, h: 72);
        File.WriteAllBytes(cards[0].FullPath, TestScenes.Encode(edited, MagickFormat.Jpeg, 90));

        _ = WpfHost.Run(() => cards[0].Picture);
        Assert.True(SpinWait.SpinUntil(() => WpfHost.Run(() => cards[0].Picture != null), TimeSpan.FromSeconds(10)));
        _ = WpfHost.Run(() => cards[1].Picture);
        Assert.True(SpinWait.SpinUntil(() => WpfHost.Run(() => cards[1].Picture != null), TimeSpan.FromSeconds(10)));
        var images = WpfHost.Run(() => (cards[0].Picture, cards[1].Picture));

        Assert.NotSame(images.Item1, images.Item2);
        static byte[] Pixels(System.Windows.Media.ImageSource? image)
        {
            var bitmap = (System.Windows.Media.Imaging.BitmapSource)image!;
            int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        Assert.False(Pixels(images.Item1).SequenceEqual(Pixels(images.Item2)), "Each card should show its own photo.");
    }

    [Fact]
    public async Task Closing_services_waits_for_thumbnail_work_before_closing_the_picture_store()
    {
        using var ws = new TestWorkspace("thumbnail-dispose");
        using var scene = TestScenes.Scene(902, w: 96, h: 72);
        var path = ws.Photo("photo.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 90));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ws.Fs.BeforeOpen = _ => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var services = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);
        try
        {
            services.Thumbnails.Request("picture-key", path, () => { });
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            var disposing = Task.Run(services.Dispose);
            try
            {
                await Task.Delay(200);
                Assert.False(disposing.IsCompleted, "Services closed while picture work was still using the store.");
            }
            finally { release.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Every_card_waiting_for_a_prefetched_picture_is_notified_when_it_is_ready()
    {
        using var ws = new TestWorkspace("thumbnail-notify");
        using var scene = TestScenes.Scene(901, w: 96, h: 72);
        var path = ws.Photo("photo.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 90));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var firstNotified = new ManualResetEventSlim();
        using var secondNotified = new ManualResetEventSlim();
        ws.Fs.BeforeOpen = _ => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var service = new ThumbnailService(ws.Fs, ws.Log);

        try
        {
            service.Prefetch(new[] { ("picture-key", path) });
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "Prefetch did not start reading the picture.");
            service.Request("picture-key", path, () => firstNotified.Set());
            service.Request("picture-key", path, () => secondNotified.Set());
            release.Set();

            Assert.True(firstNotified.Wait(TimeSpan.FromSeconds(10)), "The first waiting card was not notified.");
            Assert.True(secondNotified.Wait(TimeSpan.FromSeconds(10)), "The second waiting card was not notified.");
            Assert.NotNull(service.TryGet("picture-key"));
        }
        finally { release.Set(); }
    }
}
