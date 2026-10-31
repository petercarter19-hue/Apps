using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

[Collection("WPF")]
public class StopPreflightTests
{
    [Fact]
    public async Task Stop_during_removal_preflight_prevents_any_photo_from_moving()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        using var e = new Step3Env("stop-preflight", extraSharedProbe: _ =>
        {
            if (block) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            return null;
        });
        var paths = e.Identical(912, "a.jpg", "b.jpg");
        await e.ScanAsync();
        e.S.Shared.ForgetEverything();
        block = true;
        var task = WpfHost.Run(() => e.Vm.RemoveGroupAsync(e.Vm.ExactGroups.Single()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            WpfHost.Run(() => e.Vm.StopCommand.Execute(null));
        }
        finally { release.Set(); }
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        Assert.Empty(e.S.Journal.Entries());
        Assert.False(e.Vm.IsBusy);
    }
}
