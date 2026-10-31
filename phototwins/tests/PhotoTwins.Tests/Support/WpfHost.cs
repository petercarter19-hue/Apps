using System.Windows;
using System.Windows.Threading;

namespace PhotoTwins.Tests.Support;

/// <summary>One shared background thread that owns the WPF application for all UI tests (WPF allows only one per program).</summary>
public static class WpfHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher != null) return;
            var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
                PhotoTwins.Glass.Forced = true;       // the tests run with the glass look on, whatever this PC's own setting is
                PhotoTwins.Glass.Refresh(true);
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "WPF test thread" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
        }
    }

    public static T Run<T>(Func<T> f)
    {
        EnsureStarted();
        return _dispatcher!.Invoke(f);
    }

    public static void Run(Action a) => Run<int>(() => { a(); return 0; });

    public static void SetTheme(bool dark) => Run(() =>
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        DoEvents();
        PhotoTwins.Glass.Refresh();
    });

    /// <summary>Lets WPF finish pending layout and binding work (never needs a real window).</summary>
    public static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
}
