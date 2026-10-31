using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins;

/// <summary>
/// Draws the pictures shown on screen. Each comes from the picture memory (small pictures made during the scan, so they appear at once), or,
/// if it isn't there, is made from a read-only look at the photo and remembered. Several are made at the same time; the most recent ones are
/// kept so scrolling back is instant. A picture that can't be drawn says why (and is logged), never a blank.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int KeepCount = 300;

    private readonly IFileSystem _fs;
    private readonly AppLog _log;
    private readonly ThumbnailStore? _store;
    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount - 2, 3, 8));
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string key, ImageSource image)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string key, ImageSource image)> _order = new();
    private readonly Dictionary<string, List<(SynchronizationContext? context, Action done)>> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ThumbnailService(IFileSystem fs, AppLog log, ThumbnailStore? store = null) { _fs = fs; _log = log; _store = store; }

    private static string MemoryKey(string key, string path) => key + "\0" + path;

    public ImageSource? TryGet(string key, string path) => TryGetMemory(MemoryKey(key, path));

    // Compatibility for service callers that only have a content key.
    public ImageSource? TryGet(string key)
    {
        lock (_lock)
        {
            var found = _cache.Keys.FirstOrDefault(k => k.StartsWith(key + "\0", StringComparison.OrdinalIgnoreCase));
            return found == null ? null : TryGetMemory(found);
        }
    }

    private ImageSource? TryGetMemory(string key)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var node)) return null;
            _order.Remove(node); _order.AddFirst(node);
            return node.Value.image;
        }
    }

    /// <summary>Why this picture couldn't be drawn, or null.</summary>
    public string? FailureOf(string key, string path) { lock (_lock) return _failed.TryGetValue(MemoryKey(key, path), out var why) ? why : null; }
    public string? FailureOf(string key) { lock (_lock) return _failed.FirstOrDefault(p => p.Key.StartsWith(key + "\0", StringComparison.OrdinalIgnoreCase)).Value; }

    /// <summary>
    /// Forgets which pictures failed, so each is tried again. Called when new scan results are shown: a picture can fail for a passing reason
    /// (OneDrive busy with the file, a drive asleep) and must not stay failed until the app is restarted.
    /// </summary>
    public void ClearFailures() { lock (_lock) _failed.Clear(); }

    /// <summary>Starts drawing the picture if it isn't drawn, being drawn, or known to be undrawable. <paramref name="done"/> runs when finished.</summary>
    public void Request(string key, string path, Action done)
    {
        var context = SynchronizationContext.Current;
        var memoryKey = MemoryKey(key, path);
        bool ready;
        lock (_lock)
        {
            if (_disposed) return;
            ready = _cache.ContainsKey(memoryKey) || _failed.ContainsKey(memoryKey);
            if (!ready)
            {
                if (_loading.TryGetValue(memoryKey, out var waiters)) { waiters.Add((context, done)); return; }
                _loading[memoryKey] = new() { (context, done) };
            }
        }
        if (ready) { Notify(context, done); return; }
        _ = Task.Run(async () =>
        {
            bool gated = false;
            try
            {
                // 1. the picture memory: a small JPEG, nothing is opened (an online-only photo is shown too, never downloaded)
                byte[]? jpeg = _store?.TryGet(key);
                if (jpeg == null)
                {
                    // 2. not there: make it from the photo (the photo is only ever read; online-only ones are refused)
                    await _gate.WaitAsync();
                    gated = true;
                    var bytes = PhotoPictures.ReadBytes(_fs, path);
                    jpeg = PhotoPictures.MakeThumb(bytes);
                    // remember it only if the photo is still the one that was scanned (it may have been edited since)
                    if (_store != null && ThumbnailStore.KeyOf(bytes.Length, SHA256.HashData(bytes)) == key) _store.Put(key, jpeg);
                }
                var image = ToImage(jpeg);
                lock (_lock)
                {
                    if (_cache.TryGetValue(memoryKey, out var existing)) _order.Remove(existing);
                    var node = _order.AddFirst((memoryKey, image));
                    _cache[memoryKey] = node;
                    while (_order.Count > KeepCount) { _cache.Remove(_order.Last!.Value.key); _order.RemoveLast(); }
                }
            }
            catch (Exception ex)
            {
                var why = PhotoPictures.WhyNotShown(ex);
                _log.Warn($"Couldn't draw {path}: {why}");
                lock (_lock) _failed[memoryKey] = "Couldn't draw this picture. " + why;
            }
            finally
            {
                if (gated) _gate.Release();
                List<(SynchronizationContext? context, Action done)> waiters;
                lock (_lock) { waiters = _loading[memoryKey]; _loading.Remove(memoryKey); Monitor.PulseAll(_lock); }
                foreach (var waiter in waiters) Notify(waiter.context, waiter.done);
            }
        });
    }

    private static void Notify(SynchronizationContext? context, Action done)
    {
        if (context != null) context.Post(_ => done(), null); else done();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            while (_loading.Count > 0) Monitor.Wait(_lock);
        }
        _gate.Dispose();
    }

    /// <summary>Starts getting the pictures that are about to be looked at (the first groups), so they are ready when the cards appear.</summary>
    public void Prefetch(IEnumerable<(string key, string path)> pictures)
    {
        foreach (var (key, path) in pictures) Request(key, path, () => { });
    }

    public static ImageSource ToImage(byte[] bmp)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bmp);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
