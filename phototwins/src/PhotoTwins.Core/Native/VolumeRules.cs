using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PhotoTwins.Core.Native;

/// <summary>
/// Decides, drive by drive, whether Windows' Recycle Bin can take a photo. Anything that can't go to the Recycle Bin
/// (USB sticks, memory cards, network drives, substituted drive letters, a Recycle Bin that is switched off or too small
/// for the file) is moved to a "Duplicates to delete" folder instead. This is the check made BEFORE Windows is asked to
/// recycle anything; a second guard inside the recycling call itself cancels any delete that would not be recycled.
/// </summary>
public class VolumeRules
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, uint max);
    [StructLayout(LayoutKind.Sequential)]   // normal 64-bit layout (24 bytes); Windows rejects a packed one
    private struct SHQUERYRBINFO { public uint cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string rootPath, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);

    private const int PathLimit = 240;

    /// <summary>Null if the Recycle Bin can take this photo; otherwise a plain reason it can't.</summary>
    public virtual string? WhyNotRecyclable(string path, long fileSize)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return "it is on a network location";
            if (path.Length >= PathLimit) return "its path is too long for the Recycle Bin";
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.Length < 2) return "Windows can't say which drive it is on";
            if (GetDriveTypeW(root) != 3) return "its drive has no Recycle Bin (USB stick, memory card or network drive)";
            if (IsSubstitutedDrive(root)) return "its drive letter is a shortcut to a folder, which has no Recycle Bin of its own";
            if (PolicyTurnsRecycleBinOff()) return "Windows is set to delete files instead of recycling them";

            var guid = VolumeGuid(root);
            long? maxMb = null;
            bool nuke = false;
            using (var bin = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket"))
            {
                if (bin != null)
                {
                    if (bin.GetValue("NukeOnDelete") is int g && g != 0) nuke = true;
                    if (bin.GetValue("MaxCapacity") is int gm && gm > 0) maxMb = gm;
                }
            }
            if (guid != null)
            {
                using var v = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);
                if (v != null)
                {
                    if (v.GetValue("NukeOnDelete") is int n) nuke = n != 0;
                    if (v.GetValue("MaxCapacity") is int m && m > 0) maxMb = m;
                }
            }
            if (nuke) return "the Recycle Bin is switched off for that drive";
            long? cap = maxMb is { } mb ? mb * 1024L * 1024 : DefaultBinCapacity(new DriveInfo(root).TotalSize);
            if (cap == null) return "Windows couldn't say how big the Recycle Bin is on that drive";
            long capacity = cap.Value;
            if (fileSize > capacity * 9 / 10) return "it is too big for the Recycle Bin on that drive";
            return null;
        }
        catch (Exception ex)
        {
            return "Windows couldn't say whether its drive has a Recycle Bin (" + ex.Message + ")";   // unsure: do the safe thing
        }
    }

    /// <summary>The most the Recycle Bin on this drive holds before Windows starts permanently deleting its oldest items. Null if unknown.</summary>
    public virtual long? BinCapacityBytes(string root)
    {
        try
        {
            long? maxMb = null;
            using (var bin = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket"))
                if (bin?.GetValue("MaxCapacity") is int gm && gm > 0) maxMb = gm;
            var guid = VolumeGuid(root);
            if (guid != null)
            {
                using var v = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);
                if (v?.GetValue("MaxCapacity") is int m && m > 0) maxMb = m;
            }
            return maxMb is { } mb ? mb * 1024L * 1024 : DefaultBinCapacity(new DriveInfo(root).TotalSize);
        }
        catch { return null; }
    }

    /// <summary>
    /// Windows' own default Recycle Bin size when the user never set one: 10% of the first 40 GB of the drive plus 5% of the rest.
    /// Null if the drive's size is unknown (then the safe route is used).
    /// </summary>
    public static long? DefaultBinCapacity(long driveTotalBytes)
    {
        if (driveTotalBytes <= 0) return null;
        const long forty = 40L * 1024 * 1024 * 1024;
        return (long)(Math.Min(driveTotalBytes, forty) * 0.10 + Math.Max(0, driveTotalBytes - forty) * 0.05);
    }

    /// <summary>How much is in this drive's Recycle Bin right now (read from Windows). Null if Windows won't say.</summary>
    public virtual long? BinUsedBytes(string root)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBinW(root, ref info) == 0 ? info.i64Size : null;
        }
        catch { return null; }
    }

    private static bool IsSubstitutedDrive(string root)
    {
        var sb = new StringBuilder(512);
        var dev = root.TrimEnd('\\');   // "Z:"
        return QueryDosDeviceW(dev, sb, (uint)sb.Capacity) > 0 && sb.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private static bool PolicyTurnsRecycleBinOff()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
            if (k?.GetValue("NoRecycleFiles") is int v && v != 0) return true;
        }
        return false;
    }

    private static string? VolumeGuid(string root)
    {
        var sb = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, sb, (uint)sb.Capacity)) return null;
        var s = sb.ToString();   // \\?\Volume{guid}\
        int a = s.IndexOf('{'), b = s.IndexOf('}');
        return a >= 0 && b > a ? s.Substring(a, b - a + 1) : null;
    }
}

/// <summary>
/// Keeps a running total for ONE removal action, drive by drive: how full each drive's Recycle Bin is, plus everything this
/// action has already put in it. Windows permanently deletes the OLDEST items in a bin that overflows, which could be Pete's other
/// deleted files or photos removed earlier, so a photo that would tip the bin over goes to the "Duplicates to delete" folder instead.
/// </summary>
public sealed class BinLedger
{
    private const double Headroom = 0.9;   // never plan to fill more than 90% of the bin
    private const long RequeryEvery = 1L << 30;

    private sealed class Drive { public long Capacity; public long Used; public long Added; }

    private readonly VolumeRules _rules;
    private readonly Dictionary<string, Drive> _drives = new(StringComparer.OrdinalIgnoreCase);

    public BinLedger(VolumeRules rules) { _rules = rules; }

    /// <summary>Null if this photo fits in its drive's bin on top of what is already in it and what this action has added; otherwise why not.</summary>
    public string? WhyNoRoom(string path, long size)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return "Windows can't say which drive it is on";
        lock (_drives)
        {
            if (!_drives.TryGetValue(root, out var d))
            {
                var cap = _rules.BinCapacityBytes(root);
                var used = _rules.BinUsedBytes(root);
                if (cap == null || used == null) return "Windows couldn't say how full the Recycle Bin is on that drive";
                _drives[root] = d = new Drive { Capacity = cap.Value, Used = used.Value };
            }
            if (d.Used + d.Added + size > d.Capacity * Headroom)
                return "the Recycle Bin on that drive is nearly full: putting more in would make Windows permanently delete older items in it";
            return null;
        }
    }

    /// <summary>Records a photo that really went into the bin.</summary>
    public void Added(string path, long size)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return;
        lock (_drives)
        {
            if (!_drives.TryGetValue(root, out var d)) return;
            d.Added += size;
            if (d.Added >= RequeryEvery)   // every gigabyte or so, look at the bin again (other programs may be using it too)
            {
                var used = _rules.BinUsedBytes(root);
                if (used != null) { d.Used = used.Value; d.Added = 0; } else d.Used += d.Added;
            }
        }
    }
}
