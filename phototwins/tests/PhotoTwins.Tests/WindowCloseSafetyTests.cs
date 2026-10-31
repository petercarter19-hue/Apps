using System.ComponentModel;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

[Collection("WPF")]
public class WindowCloseSafetyTests
{
    private static bool CloseRequested(System.Windows.Window window)
    {
        var e = new CancelEventArgs();
        window.GetType().GetMethod("OnClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { e });
        return e.Cancel;
    }

    [Fact]
    public async Task Closing_the_owner_waits_for_an_active_owned_undo_including_repeated_requests()
    {
        using var e = new Step3Env("close-owner-undo");
        e.Identical(919, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        e.Ws.Fs.BeforeOpen = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = WpfHost.Run(() =>
        {
            var owner = new MainWindow(e.Vm);
            new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle(); // native handle only; neither window is shown
            var undo = new UndoWindow(e.S, e.Vm) { Owner = owner };
            owner.Closed += (_, _) => closed.TrySetResult();
            return (owner, undo);
        });
        var row = windows.undo.Model.Actions.Single().AllRows.Single();
        var restoring = WpfHost.Run(() => windows.undo.Model.PutBackAsync(row.Entry));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            WpfHost.Run(() => { windows.owner.Close(); windows.owner.Close(); });
            Assert.False(closed.Task.IsCompleted, "The owner closed while Undo was still restoring a photo.");
            Assert.True(WpfHost.Run(() => windows.undo.Owner == windows.owner));
        }
        finally { release.Set(); await restoring.WaitAsync(TimeSpan.FromSeconds(10)); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(row.Entry.OriginalPath));
    }

    [Fact]
    public async Task Repeated_close_during_a_scan_cannot_bypass_the_pending_stop()
    {
        using var e = new Step3Env("close-repeat");
        e.Identical(913, "a.jpg", "b.jpg");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        e.Ws.Fs.BeforeOpen = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var scan = WpfHost.Run(() => e.Vm.StartScanAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var window = WpfHost.Run(() => new MainWindow(e.Vm));
            Assert.True(WpfHost.Run(() => CloseRequested(window)));
            Assert.True(WpfHost.Run(() => CloseRequested(window)));
        }
        finally { release.Set(); await scan.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Undo_window_cannot_close_while_a_restore_is_still_running()
    {
        using var e = new Step3Env("close-undo");
        e.Identical(914, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        e.Ws.Fs.BeforeOpen = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var window = WpfHost.Run(() => new UndoWindow(e.S, e.Vm));
        var row = window.Model.Actions.Single().AllRows.Single();
        var restoring = WpfHost.Run(() => window.Model.PutBackAsync(row.Entry));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try { Assert.True(WpfHost.Run(() => CloseRequested(window))); }
        finally { release.Set(); await restoring.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
