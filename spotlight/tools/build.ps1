# Builds Spotlight: run the headless tests, publish the single-file exe into dist\, then compile the installer into installer\out\.
# Nothing here starts Spotlight or installs it.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $SkipTests) {
    dotnet run --project tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
}
dotnet publish Spotlight.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (free: https://jrsoftware.org/isinfo.php), or build only the app: dotnet publish Spotlight.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist' }
& $iscc (Join-Path $root 'installer\Spotlight.iss')
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
Get-ChildItem (Join-Path $root 'installer\out') | Select-Object Name, Length
