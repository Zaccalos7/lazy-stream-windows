"""Exercise installer cache upgrades and failure preservation without network access."""
import hashlib
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
import zipfile


INSTALLER = Path(__file__).resolve().parents[1]
REQUIRED = [
    "ffmpeg.exe", "ffprobe.exe", "LICENSE-ffmpeg.txt", "avcodec-63.dll",
    "avdevice-63.dll", "avfilter-12.dll", "avformat-63.dll", "avutil-61.dll",
    "swresample-7.dll", "swscale-10.dll",
]


class FetchFfmpegTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="orbis-ffmpeg-fetch-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "Installer").mkdir()
        self.destination = self.root / "artifacts" / "ffmpeg"
        self.destination.mkdir(parents=True)
        self.archive = self.root / "fixture.zip"
        self.make_archive()
        self.sha = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        original = (INSTALLER / "fetch-ffmpeg.sh").read_text()
        self.script = self.root / "Installer" / "fetch-ffmpeg.sh"
        self.script.write_text(re.sub(r"sha256='[0-9a-f]{64}'", f"sha256='{self.sha}'", original))
        self.bin = self.root / "bin"
        self.bin.mkdir()
        curl = self.bin / "curl"
        curl.write_text('#!/bin/bash\nwhile [ "$#" -gt 0 ]; do\n'
                        '  if [ "$1" = "-o" ]; then cp "$FETCH_FIXTURE" "$2"; '
                        'echo fetched >> "$FETCH_LOG"; exit 0; fi\nshift\ndone\nexit 1\n')
        curl.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        FETCH_FIXTURE=str(self.archive), FETCH_LOG=str(self.root / "fetch.log"))

    def make_archive(self, missing=None):
        with zipfile.ZipFile(self.archive, "w") as archive:
            for name in REQUIRED:
                if name == missing:
                    continue
                path = "ffmpeg9/LICENSE.txt" if name == "LICENSE-ffmpeg.txt" else "ffmpeg9/bin/" + name
                archive.writestr(path, b"fixture payload")

    def fetch(self, *args):
        return subprocess.run(["bash", str(self.script), *args], env=self.env,
                              capture_output=True, text=True, timeout=10)

    def test_old_payload_is_upgraded_and_complete_payload_is_reused(self):
        for name in ("ffmpeg.exe", "ffprobe.exe", "avcodec-62.dll"):
            (self.destination / name).write_bytes(b"old")
        first = self.fetch()
        self.assertEqual(0, first.returncode, first.stderr)
        self.assertFalse((self.destination / "avcodec-62.dll").exists())
        self.assertTrue(all((self.destination / name).is_file() for name in REQUIRED))
        self.assertEqual(self.sha, (self.destination / "build.sha256").read_text().strip())
        self.assertEqual(0, self.fetch().returncode)
        self.assertEqual(1, len((self.root / "fetch.log").read_text().splitlines()))
        self.assertEqual(0, self.fetch("--force").returncode)
        self.assertEqual(2, len((self.root / "fetch.log").read_text().splitlines()))

    def test_missing_dll_refreshes_the_cache(self):
        self.assertEqual(0, self.fetch().returncode)
        (self.destination / "swscale-10.dll").unlink()
        self.assertEqual(0, self.fetch().returncode)
        self.assertTrue((self.destination / "swscale-10.dll").exists())

    def test_bad_checksum_preserves_previous_payload(self):
        old = self.destination / "ffmpeg.exe"
        old.write_bytes(b"old executable")
        self.archive.write_bytes(b"corrupted download")
        result = self.fetch()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Checksum mismatch", result.stderr)
        self.assertEqual(b"old executable", old.read_bytes())
        self.assertFalse(list((self.root / "artifacts").glob("ffmpeg-stage-*")))

    def test_incomplete_archive_preserves_previous_payload(self):
        old = self.destination / "ffmpeg.exe"
        old.write_bytes(b"old executable")
        self.make_archive(missing="avcodec-63.dll")
        actual = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        self.script.write_text(self.script.read_text().replace(self.sha, actual))
        result = self.fetch()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("avcodec-63.dll", result.stderr)
        self.assertEqual(b"old executable", old.read_bytes())

    def test_windows_and_bash_pin_the_same_archive_and_hash(self):
        bash = (INSTALLER / "fetch-ffmpeg.sh").read_text()
        powershell = (INSTALLER / "fetch-ffmpeg.ps1").read_text()
        for key in ("url", "sha256"):
            self.assertEqual(re.search(rf"{key}='([^']+)'", bash)[1],
                             re.search(rf"\${key} = '([^']+)'", powershell)[1])
        for name in REQUIRED:
            self.assertIn(name, powershell)


if __name__ == "__main__":
    unittest.main()
