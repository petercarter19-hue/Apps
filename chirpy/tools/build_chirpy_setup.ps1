# Builds Chirpy-Setup.exe (unsigned) from a package that tools/build_package.py made.
#   .\tools\build_chirpy_setup.ps1 -Package <output folder of build_package.py> -Out <folder for the installer>
#                                  -Iscc <path to Inno Setup 6's ISCC.exe> -Python <the runtime's python.exe>
# <Package> is the folder that holds "Chirpy" (Setup.cmd, app, python, models...). The installer is written to <Out>\Chirpy-Setup.exe
# with its SHA-256 beside it. Inno Setup 6 (https://jrsoftware.org/isinfo.php) is the only extra program needed. The runtime's
# python.exe only draws the wizard pictures (it has Pillow).
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Iscc,
    [Parameter(Mandatory)][string]$Python
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$pkg = Join-Path (Resolve-Path -LiteralPath $Package).Path 'Chirpy'
foreach ($need in 'Setup.cmd', 'setup.ps1', 'VERSION.txt', 'app\voicetool.py', 'app\voicetool.ico', 'python\pythonw.exe',
         'models\parakeet-tdt-0.6b-v2\encoder-model.int8.onnx', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'README.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $pkg $need))) { throw "The package has no $need ($pkg)." }
}
if (-not (Test-Path -LiteralPath $Iscc)) { throw "Inno Setup's compiler is not at $Iscc." }
$versionText = (Get-Content -LiteralPath (Join-Path $pkg 'VERSION.txt') -Raw).Trim()
if ($versionText -notmatch '^Chirpy \(from daily\) ([0-9a-f]{8})\s+Version: ([0-9]+)\.([0-9]+)\.([0-9]+)$') { throw "VERSION.txt says '$versionText'." }
$sha = $Matches[1]
$version = "$($Matches[2]).$($Matches[3]).$($Matches[4])"
$numeric = "$version.0"

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$images = Join-Path $Out 'images'
New-Item -ItemType Directory -Force -Path $images | Out-Null
& $Python -I (Join-Path $tools 'chirpy_setup_images.py') (Join-Path $pkg 'app\voicetool.ico') $images | Out-Null
if ($LASTEXITCODE) { throw "Drawing the wizard pictures failed ($LASTEXITCODE)." }

& $Iscc /Qp "/DPackageDir=$pkg" "/DChirpyVersion=$version" "/DChirpyBuild=$sha" "/DNumericVersion=$numeric" "/DImagesDir=$images" "/DOutDir=$Out" (Join-Path $tools '..\installer\chirpy.iss')
if ($LASTEXITCODE) { throw "Inno Setup's compiler failed ($LASTEXITCODE)." }
$exe = Join-Path $Out 'Chirpy-Setup.exe'
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
Set-Content -LiteralPath "$exe.sha256" -Encoding ASCII -Value "$hash  Chirpy-Setup.exe"
Write-Host "$exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB) sha256 $hash"
