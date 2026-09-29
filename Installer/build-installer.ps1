<#
.SYNOPSIS
    Builds the Orbis Stream Windows setup (NSIS) on Windows.

.DESCRIPTION
    FFmpeg is bundled as an optional component; use -NoFfmpeg to build a small
    setup that installs FFmpeg with winget instead.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1 -OutputDirectory D:\out

.NOTES
    Requires the .NET 10 SDK and NSIS (https://nsis.sourceforge.io/Download).
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $NoFfmpeg
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\installer' }
$publish = Join-Path $root 'artifacts\publish'

$version = ([regex]::Match((Get-Content (Join-Path $root 'Directory.Build.props') -Raw), '<Version>(.*?)</Version>')).Groups[1].Value
if (-not $version) { throw 'Cannot read <Version> from Directory.Build.props' }

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue)?.Source
if (-not $makensis) {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'
    if (Test-Path $candidate) { $makensis = $candidate }
}
if (-not $makensis) { throw 'makensis not found: install NSIS (https://nsis.sourceforge.io/Download)' }

$ffmpegArgs = @()
if ($NoFfmpeg) {
    Write-Host '==> Building without the FFmpeg payload (the setup will use winget)'
}
else {
    & (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1')
    $ffmpegArgs = "-DFFMPEGDIR=$(Join-Path $root 'artifacts\ffmpeg')"
}

Write-Host "==> Publishing Orbis Stream $version (win-x64, self-contained, single file)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Orbis.Stream.App\Orbis.Stream.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '==> Compiling the setup'
if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# makensis resolves File/!include paths against the working directory.
Push-Location $PSScriptRoot
try {
    & $makensis `
        "-DAPPVERSION=$version" `
        "-DPUBLISHDIR=..\artifacts\publish" `
        "-DOUTPUTDIR=$OutputDirectory" `
        $ffmpegArgs `
        -V3 `
        installer.nsi
    if ($LASTEXITCODE -ne 0) { throw 'makensis failed' }
}
finally {
    Pop-Location
}

$setup = Join-Path $OutputDirectory "OrbisStream-$version-win-x64-setup.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup but the setup was not created" }
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "==> $setup ($size MB)"
