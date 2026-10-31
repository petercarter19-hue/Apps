using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoTwins.Core.Native;

/// <summary>Which file this really is on disk: the drive's serial number plus Windows' own file ID. A moved or renamed file keeps both.</summary>
public readonly record struct FileIdentity(uint VolumeSerial, ulong FileId);

/// <summary>Facts about an already-open file, all read from the open handle in one call (nothing is read from the file's contents).</summary>
public readonly record struct HandleInfo(FileAttributes Attributes, long Size, long LastWriteUtcTicks, FileIdentity Identity, uint LinkCount);

public static class HandleInfoReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    public static HandleInfo Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var i))
            throw new IOException("Windows wouldn't tell us about the open file.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        long size = ((long)i.nFileSizeHigh << 32) | i.nFileSizeLow;
        long ft = ((long)i.ftLastWriteTime.High << 32) | i.ftLastWriteTime.Low;
        long ticks = DateTime.FromFileTimeUtc(ft).Ticks;
        ulong id = ((ulong)i.nFileIndexHigh << 32) | i.nFileIndexLow;
        return new HandleInfo((FileAttributes)i.dwFileAttributes, size, ticks, new FileIdentity(i.dwVolumeSerialNumber, id), i.nNumberOfLinks);
    }
}
