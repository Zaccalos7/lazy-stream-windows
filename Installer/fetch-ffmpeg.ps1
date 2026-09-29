<#
.SYNOPSIS
    Downloads the FFmpeg build that the installer bundles and stages it in
    artifacts/ffmpeg. The download is pinned to an immutable BtbN release tag and
    verified against its published SHA-256.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Installer\fetch-ffmpeg.ps1
#>
[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'artifacts\ffmpeg'

# BtbN/FFmpeg-Builds, shared build, GPL (libx264 is required by the Twitch preset).
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-25-15-37/ffmpeg-n8.1.3-win64-gpl-shared-8.1.zip'
$sha256 = '1b3f0a730b1a3d780cce438d2fe091c5b8473dad2a5dbf0c2d0625029b3e70b6'

if (-not $Force -and (Test-Path (Join-Path $destination 'ffmpeg.exe')) -and (Test-Path (Join-Path $destination 'ffprobe.exe'))) {
    Write-Host "==> FFmpeg already staged in $destination"
    return
}

Write-Host '==> Downloading FFmpeg (this happens once, the result is cached)'
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "ffmpeg-$([guid]::NewGuid().ToString('N')).zip"
try {
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    Write-Host '==> Verifying the SHA-256'
    $actual = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "Checksum mismatch ($actual): the download is corrupted, refusing to continue"
    }

    Write-Host "==> Staging the binaries in $destination"
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    # The DLLs of the shared build must sit next to the executables; ffplay is unused.
    $wanted = 'ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.Name
            $inBin = $entry.FullName -match '/bin/[^/]+$'
            $isDll = $name -match '^(av|sw)[a-z]+-\d+\.dll$'
            if (-not ($inBin -and (($name -in $wanted) -or $isDll))) { continue }

            $target = if ($name -eq 'LICENSE.txt') { 'LICENSE-ffmpeg.txt' } else { $name }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $target), $true)
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    Remove-Item $archive -ErrorAction SilentlyContinue
}

foreach ($file in 'ffmpeg.exe', 'ffprobe.exe', 'LICENSE-ffmpeg.txt') {
    if (-not (Test-Path (Join-Path $destination $file))) {
        throw "The archive does not contain $file"
    }
}

Write-Host "==> FFmpeg staged in $destination"
