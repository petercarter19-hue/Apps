using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Scanning;

/// <summary>One item in a folder listing. Built from the listing alone: no file is opened to make one.</summary>
public readonly record struct FileEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    FileAttributes Attributes,
    long Length,
    long LastWriteUtcTicks,
    bool IsLink,
    long CreatedUtcTicks = 0);

/// <summary>An opened photo file (read-only) plus what Windows says about it. Dispose to close.</summary>
public sealed class OpenedFile : IDisposable
{
    public FileStream Stream { get; }
    public HandleInfo Info { get; }
    public OpenedFile(FileStream stream, HandleInfo info) { Stream = stream; Info = info; }
    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// Everything the scanner is allowed to do to the disk. It can only list folders, look at attributes and open files
/// for reading: there is no way to write, move or delete through it. Tests slot in a watching version.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    /// <summary>Lists one folder (names, sizes, dates, attributes). May throw if Windows refuses.</summary>
    IEnumerable<FileEntry> Enumerate(string directory);
    FileAttributes GetAttributes(string path);
    /// <summary>The real place a path leads to (links, substituted drives and mapped drives seen through).</summary>
    string GetRealPath(string path);
    /// <summary>True if the path is itself a shortcut link (a symbolic link or junction).</summary>
    bool IsLink(string path);
    /// <summary>Opens for reading only, sharing with everyone else. This is the only place a photo's contents can be reached.</summary>
    OpenedFile OpenRead(string path);
}

public sealed class RealFileSystem : IFileSystem
{
    private static readonly EnumerationOptions ListAll = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,   // a folder Windows refuses is reported, never skipped quietly
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<FileEntry> Enumerate(string directory)
    {
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ListAll))
        {
            var attrs = info.Attributes;
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            bool isLink = false;
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                // Cloud-folder placeholders are reparse points too but are ordinary photos and folders to us.
                // Shortcut links (symbolic links and junctions) are the ones that could lead outside the picked folder.
                try { isLink = info.LinkTarget != null; }
                catch { isLink = true; }   // can't tell: treat as a link and leave it alone
            }
            long length = isDir ? 0 : ((FileInfo)info).Length;
            yield return new FileEntry(info.FullName, info.Name, isDir, attrs, length, info.LastWriteTimeUtc.Ticks, isLink, info.CreationTimeUtc.Ticks);
        }
    }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public string GetRealPath(string path) => Native.FinalPath.Of(path);

    public bool IsLink(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return info.LinkTarget != null; }
        catch { return true; }   // can't tell: treat as a link and leave it alone
    }

    public OpenedFile OpenRead(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        try
        {
            return new OpenedFile(fs, HandleInfoReader.Read(fs.SafeFileHandle));
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }
}
