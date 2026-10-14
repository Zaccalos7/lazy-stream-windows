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
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-14-13-03/ffmpeg-n9.0.2-27-g7e93014c5b-win64-gpl-shared-9.0.zip'
$sha256 = 'b45ce2e404fb01c1dbb16a4df92042ca1d739a6b929bda1d727a7596e8babaf9'
$required = 'ffmpeg.exe', 'ffprobe.exe', 'LICENSE-ffmpeg.txt', 'avcodec-63.dll', 'avdevice-63.dll', 'avfilter-12.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll'
$marker = Join-Path $destination 'build.sha256'

if (-not $Force -and (Test-Path $marker) -and (Get-Content $marker -Raw).Trim() -eq $sha256) {
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $destination $_) -PathType Leaf) -or (Get-Item (Join-Path $destination $_)).Length -eq 0 })
    if ($missing.Count -eq 0) {
        Write-Host "==> FFmpeg 9.0.2 already staged in $destination"
        return
    }
}

Write-Host '==> Downloading FFmpeg (this happens once, the result is cached)'
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "ffmpeg-$([guid]::NewGuid().ToString('N')).zip"
$staging = Join-Path $root "artifacts\ffmpeg-stage-$([guid]::NewGuid().ToString('N'))"
try {
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    Write-Host '==> Verifying the SHA-256'
    $actual = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "Checksum mismatch ($actual): the download is corrupted, refusing to continue"
    }

    Write-Host "==> Staging the binaries in $destination"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    # The DLLs of the shared build must sit next to the executables; ffplay is unused.
    $wanted = 'ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.Name
            $inBin = $entry.FullName -match '/bin/[^/]+$'
            $isDll = $name -match '^(av|sw)[a-z]+-\d+\.dll$'
            $isLicense = $name -eq 'LICENSE.txt'
            if (-not ($isLicense -or ($inBin -and (($name -in $wanted) -or $isDll)))) { continue }

            $target = if ($name -eq 'LICENSE.txt') { 'LICENSE-ffmpeg.txt' } else { $name }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $target), $true)
        }
    }
    finally {
        $zip.Dispose()
    }
    foreach ($file in $required) {
        $path = Join-Path $staging $file
        if (-not (Test-Path $path -PathType Leaf) -or (Get-Item $path).Length -eq 0) {
            throw "The archive does not contain $file"
        }
    }
    Set-Content -Path (Join-Path $staging 'build.sha256') -Value $sha256 -Encoding Ascii
    # Keep the previous payload until the new one has passed every check.
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    Move-Item -Path $staging -Destination $destination
}
finally {
    Remove-Item $archive -ErrorAction SilentlyContinue
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}

Write-Host "==> FFmpeg staged in $destination"
