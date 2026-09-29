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
url='https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-25-15-37/ffmpeg-n8.1.3-win64-gpl-shared-8.1.zip'
sha256='1b3f0a730b1a3d780cce438d2fe091c5b8473dad2a5dbf0c2d0625029b3e70b6'

if [ "${1:-}" != "--force" ] && [ -f "$destination/ffmpeg.exe" ] && [ -f "$destination/ffprobe.exe" ]; then
  echo "==> FFmpeg already staged in $destination"
  exit 0
fi

for tool in curl unzip sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is required" >&2; exit 1; }
done

archive="$(mktemp -t ffmpeg-XXXXXX.zip)"
trap 'rm -f "$archive"' EXIT

echo "==> Downloading FFmpeg (this happens once, the result is cached)"
curl --fail --location --retry 3 --progress-bar -o "$archive" "$url"

echo "==> Verifying the SHA-256"
echo "$sha256  $archive" | sha256sum --check --status || {
  echo "Checksum mismatch: the download is corrupted, refusing to continue" >&2
  exit 1
}

echo "==> Staging the binaries in $destination"
rm -rf "$destination"
mkdir -p "$destination"
# -j drops the directory structure; the DLLs of the shared build must sit next to
# the executables. ffplay is not used by Orbis Stream and is left out.
unzip -q -o -j "$archive" '*/bin/ffmpeg.exe' '*/bin/ffprobe.exe' '*/bin/av*.dll' '*/bin/sw*.dll' '*/LICENSE.txt' -d "$destination"
mv "$destination/LICENSE.txt" "$destination/LICENSE-ffmpeg.txt"

for file in ffmpeg.exe ffprobe.exe LICENSE-ffmpeg.txt; do
  [ -f "$destination/$file" ] || { echo "The archive does not contain $file" >&2; exit 1; }
done

echo "==> FFmpeg staged: $(du -sh "$destination" | cut -f1) in $destination"
