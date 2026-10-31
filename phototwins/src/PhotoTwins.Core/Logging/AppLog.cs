using System.Text;

namespace PhotoTwins.Core.Logging;

/// <summary>The app's own log (one text file per day). Anything that goes wrong is written here as well as shown on screen.</summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _dir;
    public string Folder => _dir;
    /// <summary>Set if the log itself couldn't be written, so the screen can say so instead of failing silently.</summary>
    public string? WriteFailure { get; private set; }

    public AppLog(string logDirectory)
    {
        _dir = logDirectory;
        try
        {
            Directory.CreateDirectory(_dir);
            PruneOld();
        }
        catch (Exception ex) { WriteFailure = ex.Message; }
    }

    public string CurrentFile => Path.Combine(_dir, $"PhotoTwins-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append(" [").Append(level).Append("] ").Append(message);
        if (ex != null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e) { WriteFailure = e.Message; }
        }
    }

    // Our own log files only: logs older than 30 days are tidied away.
    private void PruneOld()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "PhotoTwins-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
            }
            catch { /* an old log we can't tidy is not worth a message */ }
        }
    }
}
