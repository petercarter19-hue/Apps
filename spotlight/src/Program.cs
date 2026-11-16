namespace Spotlight;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Test copies (SPOTLIGHT_TEST_NS) have their own names for everything; a bad test setting must never fall back to the real ones.
        if (!Instance.EnvironmentIsUsable(out _)) { Environment.ExitCode = 2; return; }

        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitEventName);
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase)) { quitEvent.Set(); return; } // used by the installer and uninstaller

        // "--pick-area" (from the tray menu) lets the user draw an area, saves it, and exits.
        if (args.Contains("--pick-area", StringComparer.OrdinalIgnoreCase)) { Start(); AreaPicker.PickAndSave(); return; }

        // "--settings" (from the tray) opens the settings window only.
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) { Start(); SettingsForm.ShowOrFocus(NearArg(args)); return; }

        var mutex = new Mutex(true, Instance.MutexName, out bool first);
        // Already running (it is in the tray): launching Spotlight again, say from the Start menu, opens its settings window.
        // This copy must let go of the lock first: if it kept the handle, the lock would outlive the tray copy, the settings window
        // would never notice the tray had quit, and the installer would wait for it forever (found in the Sandbox, upgrade test).
        if (!first) { mutex.Dispose(); Start(); SettingsForm.ShowOrFocus(); return; }

        Start();
        Application.Run(new TrayApp(quitEvent));
        GC.KeepAlive(mutex); // the single-copy lock is held for as long as this tray copy runs
    }

    /// <summary>"--near=left,top,right,bottom": the quick bar's pill; the settings window opens beneath it.</summary>
    private static Box? NearArg(string[] args)
    {
        var a = args.FirstOrDefault(x => x.StartsWith("--near=", StringComparison.OrdinalIgnoreCase));
        if (a is null) return null;
        var p = a[7..].Split(',');
        return p.Length == 4 && p.All(v => int.TryParse(v, out _)) ? new Box(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])) : null;
    }

    private static void Start()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject as Exception);
    }
}
