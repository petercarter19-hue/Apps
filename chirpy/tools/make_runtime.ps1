# Makes the Python runtime that Chirpy ships with: a copy of a normal 64-bit Python 3.13 for Windows with the pinned packages
# of installer\requirements-friend.txt installed into it (each file checked against its SHA-256).
#   .\tools\make_runtime.ps1 -PythonHome <folder of an installed Python 3.13, the one holding python.exe> -Out <new empty folder>
# Needs internet (packages come from pypi.org). Run it from PowerShell. The Visual C++ files that onnxruntime needs are copied from
# Windows' own System32 folder (they are there when the "Microsoft Visual C++ Redistributable" is installed; Python's installer or
# https://aka.ms/vs/17/release/vc_redist.x64.exe puts them there).
param(
    [Parameter(Mandatory)][string]$PythonHome,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
$chirpy = Split-Path -Parent $PSScriptRoot
$py = Join-Path $PythonHome 'python.exe'
if (-not (Test-Path -LiteralPath $py)) { throw "No python.exe in $PythonHome." }
$ver = & $py -c "import sys; print('%d.%d' % sys.version_info[:2], 'x64' if sys.maxsize > 2**32 else 'x86')"
if ($ver -ne '3.13 x64') { throw "Chirpy needs 64-bit Python 3.13 (this is $ver)." }
if ((Test-Path -LiteralPath $Out) -and (Get-ChildItem -LiteralPath $Out -Force | Select-Object -First 1)) { throw "$Out is not empty." }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# Python itself, without any packages that happen to be installed in it (Chirpy ships only the pinned ones).
robocopy $PythonHome $Out /E /R:2 /W:2 /NP /NFL /NDL /NJH /NJS /XD __pycache__ site-packages | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying Python failed ($LASTEXITCODE)." }
$python = Join-Path $Out 'python.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $Out 'Lib\site-packages') | Out-Null
& $python -s -m ensurepip --default-pip | Out-Null   # (pip itself, only used here; the package build leaves it out)
if ($LASTEXITCODE) { throw "ensurepip failed ($LASTEXITCODE)." }

# The pinned list was made from two repackaged files (see THIRD-PARTY-NOTICES.md): a sounddevice wheel without its three
# ASIO-enabled DLLs, and a proxy-tools wheel made from its source download. So the published files are installed here instead,
# each checked against its own hash, and the three ASIO DLLs are removed below.
$pins = @(Get-Content -LiteralPath (Join-Path $chirpy 'installer\requirements-friend.txt') | Where-Object { $_ -match '^[A-Za-z]' -and $_ -notmatch '^(proxy-tools|sounddevice)' })
$pins += 'sounddevice==0.5.6 --hash=sha256:7f4162f514f007b0bf25a3ccfed3f1705bc2ec311888a90232729eec4f57a4f4'
$list = Join-Path $Out 'requirements-pinned.tmp'
Set-Content -LiteralPath $list -Value $pins -Encoding ASCII
& $python -s -m pip install --require-hashes --no-deps --only-binary=:all: --no-cache-dir -r $list
if ($LASTEXITCODE) { throw "Installing the pinned packages failed ($LASTEXITCODE)." }
$proxy = Join-Path $Out 'requirements-proxy.tmp'
Set-Content -LiteralPath $proxy -Encoding ASCII -Value 'proxy-tools==0.1.0 --hash=sha256:ccb3751f529c047e2d8a58440d86b205303cf0fe8146f784d1cbcd94f0a28010'
& $python -s -m pip install --require-hashes --no-deps --no-cache-dir -r $proxy
if ($LASTEXITCODE) { throw "Installing proxy-tools failed ($LASTEXITCODE)." }
Remove-Item -LiteralPath $list, $proxy

# Leave out the three ASIO-enabled PortAudio DLLs, as the notices say.
foreach ($asio in 'libportaudio32bit-asio.dll', 'libportaudio64bit-asio.dll', 'libportaudioarm64-asio.dll') {
    Remove-Item -LiteralPath (Join-Path $Out "Lib\site-packages\_sounddevice_data\portaudio-binaries\$asio") -ErrorAction SilentlyContinue
}

# The runtime leaves the person's own Python packages out.
Copy-Item -LiteralPath (Join-Path $chirpy 'installer\sitecustomize.py') -Destination (Join-Path $Out 'Lib\site-packages\sitecustomize.py')

# The Visual C++ files that onnxruntime needs, next to python.exe (Microsoft's app-local deployment).
foreach ($dll in 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $Out $dll))) {
        $from = Join-Path $env:SystemRoot "System32\$dll"
        if (-not (Test-Path -LiteralPath $from)) { throw "$dll is not in Windows' System32 folder: install the Visual C++ Redistributable (see the top of this file)." }
        Copy-Item -LiteralPath $from -Destination $Out
    }
}
Write-Host "Runtime ready: $Out"
