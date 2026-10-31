using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoTwins.Core.Native;

/// <summary>
/// The real place a path leads to, with shortcut links, substituted drive letters and mapped drives all seen through.
/// Used to prove a photo really is inside a folder Pete picked before anything is done to it.
/// </summary>
public static class FinalPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FILE_ID_DESCRIPTOR
    {
        [FieldOffset(0)] public uint dwSize;
        [FieldOffset(4)] public int Type;       // 0 = a plain 64-bit file ID
        [FieldOffset(8)] public long FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr security, uint flags);

    /// <summary>The path of the file or folder with this Windows file ID on this volume (the volume as its GUID, with or without braces).</summary>
    public static string OfFileId(string volumeGuid, long fileId)
    {
        var guid = volumeGuid.Trim('{', '}');
        using var volume = CreateFileW($@"\\?\Volume{{{guid}}}", 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (volume.IsInvalid) throw new IOException("Windows couldn't open that drive.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var d = new FILE_ID_DESCRIPTOR { dwSize = 24, Type = 0, FileId = fileId };
        using var h = OpenFileById(volume, ref d, 0, 7, IntPtr.Zero, 0x02000000);
        if (h.IsInvalid) throw new IOException("Windows couldn't find that folder.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return PathOfHandle(h, "that folder");
    }

    public static string Of(string path)
    {
        // access 0: only asks Windows about the file, never reads or changes it. Backup semantics lets this work for folders too.
        using var h = CreateFileW(NativePath.Extended(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Windows couldn't find where {path} really is.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return PathOfHandle(h, path);
    }

    private static string PathOfHandle(SafeFileHandle h, string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) throw new IOException($"Windows couldn't say where {path} really is.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        if (n > sb.Capacity) { sb = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); }
        return NativePath.NormalizedResult(sb.ToString());
    }
}
