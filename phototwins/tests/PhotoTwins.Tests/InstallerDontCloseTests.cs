using System.Diagnostics;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class InstallerDontCloseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DontClose_never_sends_quit_and_does_not_swap_an_open_app(bool running)
    {
        using var ws = new TestWorkspace("installer-no-close");
        var appRoot = Path.Combine(ws.Root, "installer");
        Directory.CreateDirectory(Path.Combine(appRoot, "tools"));
        Directory.CreateDirectory(Path.Combine(appRoot, "app"));
        var script = Path.Combine(appRoot, "tools", "install.ps1");
        File.Copy(Path.Combine(Directory.GetParent(TestWorkspace.TestWorkRoot())!.FullName, "tools", "install.ps1"), script);
        var installed = Path.Combine(appRoot, "app", "PhotoTwins.exe");
        File.WriteAllText(installed, "old-build");
        var wrapper = Path.Combine(ws.Root, "wrapper.ps1");
        File.WriteAllText(wrapper, """
            $ErrorActionPreference = 'Stop'
            function dotnet { New-Item -ItemType Directory -Force (Join-Path $env:TEST_APP_ROOT 'dist-next') | Out-Null; Set-Content -LiteralPath (Join-Path $env:TEST_APP_ROOT 'dist-next\PhotoTwins.exe') -Value 'new-build'; $global:LASTEXITCODE = 0 }
            function Get-Process { [CmdletBinding()] param($Name) if ($env:TEST_RUNNING -eq 'yes') { return [pscustomobject]@{ Id = 123 } } }
            function Start-Process { [CmdletBinding()] param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, $WindowStyle) if ($ArgumentList -contains '--quit') { Add-Content -LiteralPath $env:TEST_CALLS 'quit' }; if ($PassThru) { return [pscustomobject]@{ ExitCode = 0 } } }
            function New-Object { param($ComObject) $global:fakeShortcut = [pscustomobject]@{TargetPath='';WorkingDirectory='';Description='';IconLocation=''}; $global:fakeShortcut | Add-Member -MemberType ScriptMethod -Name Save -Value {}; $shell = [pscustomobject]@{}; $shell | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { param($path) return $global:fakeShortcut }; return $shell }
            & (Join-Path $env:TEST_APP_ROOT 'tools\install.ps1') -DontClose
            """);
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass"); psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(wrapper);
        psi.Environment["TEST_APP_ROOT"] = appRoot;
        psi.Environment["TEST_CALLS"] = Path.Combine(ws.Root, "calls.txt");
        psi.Environment["TEST_RUNNING"] = running ? "yes" : "no";
        psi.Environment["APPDATA"] = ws.Root;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000));
        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.False(File.Exists(psi.Environment["TEST_CALLS"]!), "DontClose sent a quit signal.");
        Assert.Equal(running ? "old-build" : "new-build", File.ReadAllText(installed).Trim());
    }
}
