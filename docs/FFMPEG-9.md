# FFmpeg 9 in Orbis Stream

The installer uses the stable **release/9.0** branch: FFmpeg 9.0.2 “Lei”,
with 27 subsequent branch fixes (`n9.0.2-27-g7e93014c5b`). FFmpeg's
[release page](https://ffmpeg.org/download.html#release_9.0) does not designate it LTS.

## Changes in the app

- Animated WebP overlays play, loop and retain transparency on the canvas. Static WebP
  remains a picture decoded once. The library and first-frame previews accept both.
  FFmpeg 9 cannot reliably seek these inputs with `-stream_loop`: the session uses
  native looping on a temporary copy whose two-byte repetition count is set to infinity.
  Encoded pixels and timings stay intact; the original is preserved and copies are deleted
  when the session is disposed.
- Saved NVIDIA presets removed by FFmpeg 9 are translated when building a command:
  `default` → `p4`, `hp` → `p1`, `hq` → `p7`, `bd` → `p5`;
  `ll`, `llhp`, `llhq` use `p4`, `p1`, `p7` with `tune=ll`;
  `lossless`, `losslesshp` use `p4`, `p1` with `tune=lossless`.
- Removed NVENC rate-control aliases use `cbr`/`vbr` and `multipass`.
  Old `2pass` and `cbr` flags are converted. These are compatibility mappings;
  modern explicit `tune`, `rc` and `multipass` values take precedence.
  The database retains the original options.
- Installer downloads verify SHA-256 and refresh an old or incomplete payload automatically.
  Download or extraction failure preserves the previous payload. Bash and PowerShell
  bundle the licence and all seven FFmpeg 9 DLLs.

## New in the FFmpeg 9 engine

The [upstream changelog](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/Changelog)
lists these additions relative to 8.1:

| Area | Additions |
| --- | --- |
| WebP | Animated decoder and demuxer |
| AMD AMF | HDR color conversion, frame-rate conversion, hardware memory mapping |
| GPU filters | `v360_vulkan`, `transpose_cuda` |
| GPU decoding | APV via Vulkan; ProRes RAW via VideoToolbox |
| Metadata | SMPTE 2094-50 passthrough; Dolby Vision multilayer HEVC splitting |
| MP4 | LCEVC track muxing |
| Audio | HE-AAC 960 decoding for DAB+ |
| Other | Playdate encoding/muxing; ONNX Runtime DNN backend with GPU providers |

CELT decoding and Ogg/CELT parsing were removed (Opus is unaffected), as were
deprecated NVENC options and SDK support before 11.1.

GPU features require compatible hardware and drivers. Upstream capabilities can
also require optional build dependencies. They are not new controls in Orbis Stream:
the app currently composes its canvas with CPU filters and detects NVENC/QSV/AMF
encoders by trial. HDR engine improvements do not turn the existing SDR live pipeline
into an HDR output pipeline. HE-AAC input still follows the existing AAC live output.

## Reproducible payload and verification

Windows payload: [BtbN autobuild-2026-10-14-13-03](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-14-13-03),
`ffmpeg-n9.0.2-27-g7e93014c5b-win64-gpl-shared-9.0.zip`.
SHA-256: `b45ce2e404fb01c1dbb16a4df92042ca1d739a6b929bda1d727a7596e8babaf9`.

Run `./Installer/fetch-ffmpeg.sh` or `Installer\fetch-ffmpeg.ps1` to stage the new
payload, then the usual installer build. `--force` / `-Force` redownloads it.
The setup built with `--no-ffmpeg` / `-NoFfmpeg` uses the version supplied by winget;
it does not pin that external installation.

Run the .NET suite with FFmpeg 9 and ffprobe 9 first on `PATH`:

```sh
dotnet test src/Orbis.Stream.Tests/Orbis.Stream.Tests.csproj
python3 Installer/tests/test_fetch_ffmpeg.py
```

`AnimatedWebpTests` checks three animation loops and the pixels behind transparent
areas through the actual composition command. It reports when the test machine's
FFmpeg lacks `webp_anim`; it requires FFmpeg 9 for this integration check.
