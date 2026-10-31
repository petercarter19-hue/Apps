using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Sharing;

/// <summary>A folder that Windows keeps in sync with an online service, and the service's name for messages.</summary>
public sealed record CloudRoot(string Path, string Service);

/// <summary>
/// Knows which folders are cloud folders (OneDrive, iCloud, Dropbox, Google Drive...). Removing a photo from one also removes it
/// online and from the user's other devices, so they are warned once before it happens.
/// </summary>
public sealed class CloudFolderDetector
{
    private readonly CloudRoot[] _roots;
    private readonly Func<string, bool>? _isGoogleDrive;

    public CloudFolderDetector(IEnumerable<CloudRoot> roots, Func<string, bool>? isGoogleDriveVolume = null)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new CloudRoot(ScanPolicy.Normalize(r.Path), r.Service))
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r => r.Path.Length).ToArray();
        _isGoogleDrive = isGoogleDriveVolume;
    }

    public IReadOnlyList<CloudRoot> Roots => _roots;

    /// <summary>The cloud service this path lives in ("OneDrive"), or null if it is an ordinary folder.</summary>
    public string? ServiceOf(string path)
    {
        var real = ResolvedPath(path);
        foreach (var r in _roots)
            if (ScanPolicy.IsSameOrInside(path, r.Path) || (real != null && ScanPolicy.IsSameOrInside(real, ResolvedPath(r.Path) ?? r.Path))) return r.Service;
        return _isGoogleDrive != null && (_isGoogleDrive(path) || (real != null && _isGoogleDrive(real))) ? "Google Drive" : null;
    }

    internal static string? ResolvedPath(string path) { try { return FinalPath.Of(path); } catch { return null; } }

    /// <summary>The cloud folders of this PC, as Windows lists them.</summary>
    public static CloudFolderDetector ForThisPc(ScanPolicy policy, PhotoTwins.Core.Logging.AppLog? log = null)
    {
        var roots = new List<CloudRoot>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (k != null)
                foreach (var id in k.GetSubKeyNames())
                {
                    using var u = k.OpenSubKey(id + @"\UserSyncRoots");
                    if (u == null) continue;
                    foreach (var name in u.GetValueNames())
                        if (name.StartsWith("S-1-", StringComparison.Ordinal) && u.GetValue(name) is string p && p.Length > 0)
                            roots.Add(new CloudRoot(p, ServiceName(id)));
                }
        }
        catch { /* the other sources below still apply */ }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (k != null)
                foreach (var acct in k.GetSubKeyNames())
                {
                    using var a = k.OpenSubKey(acct);
                    if (a?.GetValue("UserFolder") is string p && p.Length > 0) roots.Add(new CloudRoot(p, "OneDrive"));
                }
        }
        catch { }
        roots.AddRange(GoogleDriveFolders.Read(GoogleDriveFolders.DefaultDriveFsRoot(), log));
        foreach (var env in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } e) roots.Add(new CloudRoot(e, "OneDrive"));
        return new CloudFolderDetector(roots, policy.IsGoogleDriveVolume);
    }

    private static string ServiceName(string syncRootId)
    {
        var prefix = syncRootId.Split('!')[0];
        return prefix switch
        {
            "OneDrive" => "OneDrive",
            "iCloudDrive" => "iCloud Drive",
            "iCloudPhotos" => "iCloud Photos",
            _ when prefix.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase) => "Dropbox",
            _ => prefix,
        };
    }
}

/// <summary>
/// Knows which folders are shared with other people. A photo in a shared folder always stays, and removing one needs a second
/// clearly worded confirm. Sharing is read from Windows' own local records (never from the internet), and the user can also mark
/// any folder as shared himself in case Windows can't see the sharing.
/// </summary>
public sealed class SharedFolderDetector
{
    private readonly Func<string, string?>[] _probes;
    private readonly Func<IReadOnlyList<string>> _userMarked;
    private readonly Action? _refreshProbes;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="probes">Each looks at one folder and says why it is shared, or null.</param>
    /// <param name="userMarked">The folders the user marked as shared in Settings.</param>
    public SharedFolderDetector(IEnumerable<Func<string, string?>> probes, Func<IReadOnlyList<string>> userMarked, Action? refreshProbes = null)
    {
        _probes = probes.ToArray();
        _userMarked = userMarked;
        _refreshProbes = refreshProbes;
    }

    /// <summary>Why this photo counts as being in a shared folder (any folder above it counts), or null.</summary>
    public string? WhyShared(string filePath)
    {
        var real = CloudFolderDetector.ResolvedPath(filePath);
        var marked = _userMarked();
        foreach (var m in marked)
            if (ScanPolicy.IsSameOrInside(filePath, m) || (real != null && ScanPolicy.IsSameOrInside(real, CloudFolderDetector.ResolvedPath(m) ?? m))) return $"You marked \"{m}\" as shared.";
        return ProbePath(filePath) ?? (real != null && !string.Equals(real, filePath, StringComparison.OrdinalIgnoreCase) ? ProbePath(real) : null);
    }

    private string? ProbePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var why = _cache.GetOrAdd(dir, d => _probes.Select(p => { try { return p(d); } catch { return null; } }).FirstOrDefault(r => r != null));
            if (why != null) return why;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public bool IsShared(string filePath) => WhyShared(filePath) != null;

    /// <summary>Forget what was learned (after the user changes the shared-folder list or sharing changes).</summary>
    public void ForgetEverything() { _cache.Clear(); _refreshProbes?.Invoke(); }

    // ------------------------------------------------------------------ the real probes for this PC

    public static SharedFolderDetector ForThisPc(CloudFolderDetector cloud, Func<IReadOnlyList<string>> userMarked)
    {
        var shares = ReadNetworkShares();
        var probes = new List<Func<string, string?>>
        {
            // Public folder of this PC: everyone with an account here can use it
            folder => PublicFolder(folder) ? "It is in the Public folder, which everyone with an account on this PC can use." : null,
            // Network locations
            folder => folder.StartsWith(@"\\", StringComparison.Ordinal) || DriveType(folder) == 4 ? "It is on a network drive, where other people may use it." : null,
            // Folders this PC shares on the network
            folder => shares.FirstOrDefault(s => ScanPolicy.IsSameOrInside(folder, s)) is { } s ? $"It is inside \"{s}\", which this PC shares on the network." : null,
            // Google Drive: shared drives and folders other people shared with you
            folder => folder.Split('\\').Any(seg => seg.Equals("Shared drives", StringComparison.OrdinalIgnoreCase) || seg.Equals(".shortcut-targets-by-id", StringComparison.OrdinalIgnoreCase))
                ? "It is in a Google Drive folder that is shared." : null,
            // iCloud shared albums
            folder => cloud.Roots.FirstOrDefault(r => r.Service == "iCloud Photos" && ScanPolicy.IsSameOrInside(folder, r.Path)) is { } r
                      && SegmentAfter(folder, r.Path)?.Equals("Shared", StringComparison.OrdinalIgnoreCase) == true ? "It is in an iCloud shared album." : null,
            // OneDrive: Windows records the sharing status of each synced folder
            folder => cloud.ServiceOf(folder) == "OneDrive" && OneDriveSaysShared(folder) ? "OneDrive says this folder is shared with other people." : null,
        };
        return new SharedFolderDetector(probes, userMarked, () => shares = ReadNetworkShares());
    }

    private static string? SegmentAfter(string path, string root)
    {
        var rest = ScanPolicy.Normalize(path).Substring(Math.Min(ScanPolicy.Normalize(root).Length, ScanPolicy.Normalize(path).Length)).TrimStart('\\');
        return rest.Length == 0 ? null : rest.Split('\\')[0];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    private static uint DriveType(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? 0 : GetDriveTypeW(root);
    }

    private static bool PublicFolder(string folder)
    {
        var root = Path.GetPathRoot(folder);
        return !string.IsNullOrEmpty(root) && ScanPolicy.IsSameOrInside(folder, Path.Combine(root, "Users", "Public"));
    }

    private static List<string> ReadNetworkShares()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares");
            if (k == null) return list;
            foreach (var name in k.GetValueNames())
            {
                if (name.EndsWith('$')) continue;   // Windows' own hidden admin shares
                if (k.GetValue(name) is string[] parts)
                    foreach (var part in parts)
                        if (part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) list.Add(part.Substring(5));
            }
        }
        catch { /* reading the share list needs nothing special, but if it fails there is just nothing to add */ }
        return list;
    }

    private static bool OneDriveSaysShared(string folder) => StaRunner.Run(() =>
    {
        var parent = Path.GetDirectoryName(folder);
        if (string.IsNullOrEmpty(parent)) return false;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        dynamic? ns = shell.NameSpace(parent);
        dynamic? item = ns?.ParseName(Path.GetFileName(folder));
        if (item == null) return false;
        object? status = item.ExtendedProperty("System.StorageProviderSharingStatus");
        object? statuses = item.ExtendedProperty("System.StorageProviderShareStatuses");
        if (statuses is string s && s.Length > 0) return true;
        return status != null && Convert.ToUInt32(status) != 0;
    });
}
