#!/usr/bin/env bash
# Builds the Orbis Stream Windows installer.
#
#   ./Installer/build-installer.sh [output-directory] [--no-ffmpeg]
#
# Produces a self-contained setup (the .NET runtime is embedded in the payload,
# the WebView2 Evergreen Runtime ships with Windows 11), so the target machine
# needs nothing but Windows itself. FFmpeg is bundled as an optional component:
# pass --no-ffmpeg to build a small setup that installs FFmpeg with winget.
#
# Requirements:
#   * .NET 10 SDK
#   * NSIS >= 3 (makensis): https://nsis.sourceforge.io/Download
#     On Debian/Ubuntu:  sudo apt install nsis
#   * curl and unzip, only to stage FFmpeg (cached in artifacts/ffmpeg)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output=""
with_ffmpeg=1

for argument in "$@"; do
  case "$argument" in
    --no-ffmpeg) with_ffmpeg=0 ;;
    -*) echo "Unknown option $argument" >&2; exit 1 ;;
    *) output="$argument" ;;
  esac
done

output="${output:-$root/artifacts/installer}"
publish="$root/artifacts/publish"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)"

if [ -z "$version" ]; then
  echo "Cannot read <Version> from Directory.Build.props" >&2
  exit 1
fi

if ! command -v makensis >/dev/null 2>&1; then
  echo "makensis not found: install NSIS (https://nsis.sourceforge.io/Download)" >&2
  exit 1
fi

ffmpeg_args=()
if [ "$with_ffmpeg" -eq 1 ]; then
  "$root/Installer/fetch-ffmpeg.sh"
  ffmpeg_args=("-DFFMPEGDIR=$(realpath --relative-to="$root/Installer" "$root/artifacts/ffmpeg")")
else
  echo "==> Building without the FFmpeg payload (the setup will use winget)"
fi

echo "==> Publishing Orbis Stream $version (win-x64, self-contained, single file)"
rm -rf "$publish"
dotnet publish "$root/src/Orbis.Stream.App/Orbis.Stream.App.csproj" \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o "$publish"

echo "==> Compiling the setup"
rm -rf "$output"
mkdir -p "$output"
# makensis resolves File/!include paths against the working directory.
cd "$root/Installer"
makensis \
  -DAPPVERSION="$version" \
  -DPUBLISHDIR="$(realpath --relative-to="$root/Installer" "$publish")" \
  -DOUTPUTDIR="$(realpath "$output")" \
  "${ffmpeg_args[@]}" \
  -V3 \
  installer.nsi

setup="$output/OrbisStream-$version-win-x64-setup.exe"
if [ ! -f "$setup" ]; then
  echo "Expected $setup but the setup was not created" >&2
  exit 1
fi

echo "==> $setup ($(du -h "$setup" | cut -f1))"
