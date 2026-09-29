<#
.SYNOPSIS
    Builds the Orbis Stream MSI with the WiX toolset (Windows only).

.DESCRIPTION
    Alternative to build-installer.ps1 for environments that require an MSI
    (group policy deployment, silent installation from Systems Management).
    The payload is the same self-contained single-file publish, so the target
    machine needs neither the .NET runtime nor a redistributable installer.

    WiX cannot build MSI packages on Linux or macOS, this script must run on
    Windows.

.EXAMPLE
    dotnet tool install --global wix --version 5.0.2
    wix extension add -g WixToolset.UI.wixext/5.0.2
    powershell -ExecutionPolicy Bypass -File Installer\build-msi.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\installer' }
$publish = Join-Path $root 'artifacts\publish'

$version = ([regex]::Match((Get-Content (Join-Path $root 'Directory.Build.props') -Raw), '<Version>(.*?)</Version>')).Groups[1].Value
if (-not $version) { throw 'Cannot read <Version> from Directory.Build.props' }

$wix = (Get-Command wix -ErrorAction SilentlyContinue)?.Source
if (-not $wix) { throw 'wix not found: dotnet tool install --global wix --version 5.0.2' }

& (Join-Path $PSScriptRoot 'fetch-ffmpeg.ps1')
$ffmpeg = Join-Path $root 'artifacts\ffmpeg'

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

Write-Host '==> Building the MSI'
if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$msi = Join-Path $OutputDirectory "OrbisStream-$version-win-x64.msi"
& $wix build (Join-Path $PSScriptRoot 'OrbisStream.wxs') `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "PublishDir=$publish" `
    -d "FfmpegDir=$ffmpeg" `
    -d 'ProductName=Orbis Stream' `
    -d 'Manufacturer=Orbis' `
    -d "ProductVersion=$version" `
    -d 'UpgradeCode=3F1B2C64-9A5D-4E77-8C21-6D0E5B7A9F34' `
    -d 'InstallFolderName=OrbisStream' `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

Write-Host "==> $msi"
