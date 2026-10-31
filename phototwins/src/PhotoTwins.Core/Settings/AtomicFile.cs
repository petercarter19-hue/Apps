using System.Text;

namespace PhotoTwins.Core.Settings;

/// <summary>Saves the app's own small files (settings) so a crash can't leave half a file: write beside it, then swap.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
