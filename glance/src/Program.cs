namespace Glance;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Test copies (GLANCE_TEST_NS) have their own names for everything; a bad test setting must never fall back to the real ones.
        if (!Instance.EnvironmentIsUsable(out _)) { Environment.ExitCode = 2; return; }

        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            // Ask the running instance to exit, then leave.
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitEventName);
            ev.Set();
            return;
        }

        if (args.Length > 0 && args[0].Equals("--end", StringComparison.OrdinalIgnoreCase))
        {
            // The elevated helper behind the card's "As admin" button (one UAC prompt). Exit code = processes still refused.
            Environment.ExitCode = ProcessKiller.EndFromArgs(args.Skip(1));
            return;
        }

        using var mutex = new Mutex(true, Instance.MutexName, out bool first);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitEventName);
        bool restarted = false;
        if (!first)
        {
            // Opening Glance while it's already running restarts it: ask the running copy to quit, then take its place.
            quitEvent.Set();
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return; }
            catch (AbandonedMutexException) { } // the old copy exited without releasing it; it's ours now
            quitEvent.Reset(); // nothing else is running, so a leftover signal must not make the new copy quit
            restarted = true;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject as Exception);

        // --preview: write card state and renders to %LocalAppData%\Glance every second (used by the scripts in tools\).
        // --pinned: also open the card pinned at startup.
        bool preview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        bool pinned = args.Contains("--pinned", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApp(preview, pinned, restarted));
    }
}

internal static class Log
{
    public static void Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
