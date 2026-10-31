using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Sharing;

namespace PhotoTwins.Tests.Support;

/// <summary>A scripted stand-in for the questions the user is asked: records exactly what was asked, and answers as the test says.</summary>
public sealed class FakeDialogs : IDialogs
{
    private readonly Queue<DialogChoice> _answers = new();
    public List<ConfirmRequest> Requests { get; } = new();
    public List<(string title, string message)> Messages { get; } = new();
    /// <summary>What to answer when nothing is queued (default: Cancel, the safe answer).</summary>
    public DialogChoice Otherwise { get; set; } = DialogChoice.Cancel;

    public FakeDialogs Answer(params DialogChoice[] choices) { foreach (var c in choices) _answers.Enqueue(c); return this; }

    public Task<DialogChoice> ConfirmAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Otherwise);
    }

    public void ShowMessage(string title, string message) => Messages.Add((title, message));
}

/// <summary>Pretends the drive has no Recycle Bin (so removals go to the holding folder), optionally slowly, so busy states can be seen.</summary>
public sealed class NoBinRules : VolumeRules
{
    public int DelayMs { get; init; }
    public override string? WhyNotRecyclable(string path, long fileSize)
    {
        if (DelayMs > 0) Thread.Sleep(DelayMs);
        return "the test says this drive has no Recycle Bin";
    }
}

/// <summary>The app wired up the way the real one is, inside one test-work folder, with the questions scripted.</summary>
public sealed class Step3Env : IDisposable
{
    public TestWorkspace Ws { get; }
    public AppServices S { get; }
    public FakeDialogs Dialogs { get; } = new();
    public MainViewModel Vm { get; }
    public List<string> CloudFolders { get; } = new();

    private readonly bool _ownsWs;

    public Step3Env(string name, VolumeRules? volumes = null, Func<string, string?>? extraSharedProbe = null, IEnumerable<string>? cloudRelative = null, bool realBin = false, TestWorkspace? existing = null)
    {
        _ownsWs = existing == null;
        Ws = existing ?? new TestWorkspace(name);
        AppServices? holder = null;
        var cloud = new CloudFolderDetector((cloudRelative ?? Array.Empty<string>()).Select(r => new CloudRoot(Path.Combine(Ws.Pictures, r), "OneDrive")));
        var probes = new List<Func<string, string?>>();
        if (extraSharedProbe != null) probes.Add(extraSharedProbe);
        var shared = new SharedFolderDetector(probes, () => holder!.Settings.SharedFolders);
        S = new AppServices(Ws.Paths, Ws.Fs, Ws.Policy, Ws.Pictures, null,
            new AppServicesOptions { Volumes = realBin ? new VolumeRules() : (volumes ?? new NoBinRules()), Cloud = cloud, Shared = shared });
        holder = S;
        Vm = WpfHost.Run(() => new MainViewModel(S, Dialogs));
    }

    public async Task ScanAsync() => await WpfHost.Run(() => Vm.StartScanAsync());

    public async Task RemoveAsync(GroupVm g) => await WpfHost.Run(() => Vm.RemoveGroupAsync(g));

    public async Task RemoveAllExactAsync() => await WpfHost.Run(() => Vm.RemoveAllExactAsync());

    public async Task RemoveAllSamePhotoAsync() => await WpfHost.Run(() => Vm.RemoveAllSamePhotoAsync());

    public string[] Identical(int seed, params string[] relativePaths)
    {
        var bytes = TestPhotoFactory.StandIn(seed, 5000);
        return relativePaths.Select(p => Ws.Photo(p, bytes)).ToArray();
    }

    public void Dispose()
    {
        try
        {
            // leave the real Recycle Bin as found
            foreach (var e in S.Journal.Entries().Where(x => x.Status == EntryStatus.Done && x.Method == RemoveMethod.Recycle)) S.Mutations.PutBack(e);
        }
        catch { /* best effort */ }
        S.Dispose();
        if (_ownsWs) Ws.Dispose();
    }
}
