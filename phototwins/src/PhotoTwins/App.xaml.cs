using System.Windows;
using System.Windows.Threading;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;

namespace PhotoTwins;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private MainWindow? _window;
    private AppLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // --selfcheck <data folder> <picture.png>: build the screen, draw it to a picture without showing it, exit. No photos are touched.
        if (e.Args.Length >= 3 && e.Args[0] == "--selfcheck")
        {
            Shutdown(RunSelfCheck(e.Args[1], e.Args[2]));
            return;
        }

        // --quit: ask the running copy to close itself (it stops and saves first). Used by the installer. Nothing else happens.
        if (e.Args.Length >= 1 && e.Args[0] == "--quit")
        {
            SingleInstance.RequestQuit("PhotoTwins");
            Shutdown(0);
            return;
        }

        // Only one copy at a time: a second double-click just brings the first one to the front.
        _instance = new SingleInstance("PhotoTwins", () => Dispatcher.BeginInvoke(BringToFront), () => Dispatcher.BeginInvoke(() => _window?.Close()));
        if (!_instance.IsFirst)
        {
            Shutdown();
            return;
        }

        try
        {
            _services = new AppServices(AppPaths.ForThisUser(), new RealFileSystem(), ScanPolicy.ForThisPc(),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            _log = _services.Log;
            Glass.Refresh();
            _log.Info(Glass.IsOn ? "Glossy look: on." : "Glossy look: off (high contrast is on): Windows' own colors.");
            _log.Info("PhotoTwins started.");
            var vm = new MainViewModel(_services);
            _window = new MainWindow(vm);
            _window.Show();
        }
        catch (Exception ex)
        {
            TryLog("Start-up failed", ex);
            MessageBox.Show("PhotoTwins couldn't start.\n\n" + ex.Message + "\n\nDetails were saved to the log in " + AppPaths.ForThisUser().LogDir + ".",
                "PhotoTwins", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void BringToFront()
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static int RunSelfCheck(string dataDir, string png)
    {
        try
        {
            using var services = new AppServices(new AppPaths(dataDir), new RealFileSystem(), ScanPolicy.ForThisPc(), "");
            var vm = new MainViewModel(services);
            var view = new MainView { DataContext = vm };
            // the built-in image reader must load and read a picture (a one-pixel PNG) on this PC
            var reader = new PhotoTwins.Core.Imaging.PhotoAnalyzer().Analyze(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            if (reader.Error != null) { Console.Error.WriteLine("image reader: " + reader.Error); return 3; }
            SelfCheck.RenderToPng(view, 1180, 780, 1.25, png);
            var problems = SelfCheck.FindCutOffText(view);
            problems.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
            foreach (var p in problems) Console.Error.WriteLine(p);
            return problems.Count == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("Unexpected error", e.Exception);
        MessageBox.Show("Something unexpected went wrong:\n\n" + e.Exception.Message + "\n\nDetails were saved to the log. Your photos were not changed.",
            "PhotoTwins", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void TryLog(string what, Exception ex)
    {
        try { (_log ??= new AppLog(AppPaths.ForThisUser().LogDir)).Error(what, ex); } catch { /* nowhere else to write */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
