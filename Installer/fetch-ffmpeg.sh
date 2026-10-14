#!/usr/bin/env bash
# Downloads the FFmpeg build that the installer bundles and stages it in
# artifacts/ffmpeg. The download is pinned to an immutable BtbN release tag and
# verified against its published SHA-256, so a rebuild always produces the same
# installer.
#
#   ./Installer/fetch-ffmpeg.sh [--force]
#
# The result is a flat directory with ffmpeg.exe, ffprobe.exe, the FFmpeg DLLs
# (this is the "shared" build, so they must travel together) and the GPL licence.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
destination="$root/artifacts/ffmpeg"

# BtbN/FFmpeg-Builds, shared build, GPL (libx264 is required by the Twitch preset).
url='https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-14-13-03/ffmpeg-n9.0.2-27-g7e93014c5b-win64-gpl-shared-9.0.zip'
sha256='b45ce2e404fb01c1dbb16a4df92042ca1d739a6b929bda1d727a7596e8babaf9'
required=(ffmpeg.exe ffprobe.exe LICENSE-ffmpeg.txt avcodec-63.dll avdevice-63.dll avfilter-12.dll avformat-63.dll avutil-61.dll swresample-7.dll swscale-10.dll)

if [ "${1:-}" != "--force" ] && [ -f "$destination/build.sha256" ] && [ "$(cat "$destination/build.sha256")" = "$sha256" ]; then
  complete=1
  for file in "${required[@]}"; do
    [ -s "$destination/$file" ] || complete=0
  done
  if [ "$complete" -eq 1 ]; then
    echo "==> FFmpeg 9.0.2 already staged in $destination"
    exit 0
  fi
fi

for tool in curl unzip sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is required" >&2; exit 1; }
done

archive="$(mktemp -t ffmpeg-XXXXXX.zip)"
mkdir -p "$root/artifacts"
staging="$(mktemp -d "$root/artifacts/ffmpeg-stage-XXXXXX")"
trap 'rm -f "$archive"; rm -rf "$staging"' EXIT

echo "==> Downloading FFmpeg (this happens once, the result is cached)"
curl --fail --location --retry 3 --progress-bar -o "$archive" "$url"

echo "==> Verifying the SHA-256"
echo "$sha256  $archive" | sha256sum --check --status || {
  echo "Checksum mismatch: the download is corrupted, refusing to continue" >&2
  exit 1
}

echo "==> Staging the binaries in $destination"
# -j drops the directory structure; the DLLs of the shared build must sit next to
# the executables. ffplay is not used by Orbis Stream and is left out.
unzip -q -o -j "$archive" '*/bin/ffmpeg.exe' '*/bin/ffprobe.exe' '*/bin/av*.dll' '*/bin/sw*.dll' '*/LICENSE.txt' -d "$staging"
mv "$staging/LICENSE.txt" "$staging/LICENSE-ffmpeg.txt"

for file in "${required[@]}"; do
  [ -s "$staging/$file" ] || { echo "The archive does not contain $file" >&2; exit 1; }
done
echo "$sha256" > "$staging/build.sha256"
# Replace the previous payload only after download, verification and extraction succeed.
rm -rf "$destination"
mv "$staging" "$destination"

echo "==> FFmpeg staged: $(du -sh "$destination" | cut -f1) in $destination"
