# Orbis Stream

Desktop application that streams local video files to Twitch, YouTube and any other
RTMP-compatible platform.

The application is a native rewrite of the original Java Spring Boot + JCEF project:

- **Shell**: .NET 10 WPF (`OrbisStream.exe`) hosting the WebView2 control.
- **Backend**: ASP.NET Core (Kestrel) in the same process, listening on `http://localhost:1200`.
- **Frontend**: ASP.NET Core Razor Pages rendered on the server (`src/Orbis.Stream.Core/Pages`),
  styled like Windows 11 (Fluent 2 colors, Segoe UI Variable, Segoe Fluent Icons, light/dark
  theme following the system). One stylesheet and one small script, no JavaScript framework.
- **Streaming**: an external `ffmpeg` process fed by a generated FFmpeg concat playlist.
- **Persistence**: SQLite (`stream.db`), created and migrated on startup.

## Requirements

- Windows 11 (the shell is WPF/WebView2; the backend and tests also build on Linux/macOS).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- The WebView2 Runtime (preinstalled on current Windows 10/11).
- `ffmpeg` and `ffprobe` to stream: the installer bundles them (see below), otherwise
  `winget install Gyan.FFmpeg`, or copy `ffmpeg.exe` and `ffprobe.exe` next to
  `OrbisStream.exe` (the application prefers them over `PATH`).
- [NSIS](https://nsis.sourceforge.io/Download) only to build the installer
  (`sudo apt install nsis` on Debian/Ubuntu).

## Build, test, run

```bash
dotnet build OrbisStream.slnx -c Release
dotnet test OrbisStream.slnx -c Release
dotnet run --project src/Orbis.Stream.App/Orbis.Stream.App.csproj
```

The shell opens on `http://localhost:1200/orbis/mainMenu`, the dashboard of the Razor
frontend, whose router basename is `/orbis`.

## Publish

```bash
dotnet publish src/Orbis.Stream.App/Orbis.Stream.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o artifacts/publish
```

The result is self-contained: `OrbisStream.exe` embeds the .NET runtime, `wwwroot` sits
next to it, so the folder runs on a clean Windows 11 machine.

## Windows installer

```bash
./Installer/build-installer.sh          # Linux, macOS and Windows (needs makensis)
```

or on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File Installer\build-installer.ps1
```

Both publish the application and compile an NSIS setup in
`artifacts/installer/OrbisStream-<version>-win-x64-setup.exe` (about 117 MB with FFmpeg):

- installs per user in `%LOCALAPPDATA%\Programs\OrbisStream`, so no administrator rights
  are required and uninstalling never touches the videos;
- **FFmpeg 8.1.3 is bundled** in the `ffmpeg` subdirectory, so streaming works offline
  right after the installation: the application looks there before falling back to `PATH`;
- the components page lets you skip FFmpeg (or the desktop shortcut) if you prefer your own
  build; the installer then warns when neither FFmpeg nor FFprobe is on `PATH`;
- warns when the Microsoft Edge WebView2 Evergreen Runtime is missing;
- Start Menu and optional desktop shortcuts, an uninstaller registered in
  *Settings → Apps → Installed apps*;
- supports silent installation (`setup.exe /S`) and silent uninstall.

To build the small setup without the FFmpeg payload, where the FFmpeg component installs
the official build with `winget` instead:

```bash
./Installer/build-installer.sh --no-ffmpeg
```

An MSI is available for deployments that require one (Windows only, because WiX cannot
build MSI packages elsewhere):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
powershell -ExecutionPolicy Bypass -File Installer\build-msi.ps1
```


## Runtime data

Directories are resolved in this order: `--data-dir`, `LAZY_STREAM_DATA_DIR`, the current
working directory when writable, then `~/.orbis-stream`.

| Path                      | Content                                        |
| ------------------------- | ---------------------------------------------- |
| `<data>/stream.db`        | SQLite database                                |
| `<data>/logs/twitch.log`  | application log                                |
| `<data>/webview2`         | WebView2 user data folder                      |
| `<cwd>/images`            | images uploaded through `/image/upload`        |

## Configuration

| Variable                       | Command line          | Default                    |
| ------------------------------ | --------------------- | -------------------------- |
| `LAZY_STREAM_DATA_DIR`         | `--data-dir=`         | working directory / `~/.orbis-stream` |
| `LAZY_STREAM_PORT`             | `--port=`             | `1200`                     |
| `LAZY_STREAM_EMBEDDED_BROWSER` | `--browser` / `--no-browser` | `true`            |
| `LAZY_STREAM_WEB_ROOT`         | `--web-root=`         | `wwwroot` beside the executable |
| `FFMPEG_PATH`                  | –                     | `ffmpeg`                   |
| `FFPROBE_PATH`                 | –                     | `ffprobe` beside `ffmpeg`  |

## API

| Area          | Endpoints                                                                                    |
| ------------- | -------------------------------------------------------------------------------------------- |
| Live          | `POST /live/start-live`, `POST /live/start-video-live`, `PUT /live/stop-live?videoLivePkid=`    |
| Video         | `GET /video/getAllVideo`, `GET /video/getPage`, `GET /video/getAllChannelWithVideoLive`, `PUT /video/unlockVideo?videoKey=` |
| Settings      | `GET|POST /settings/retrive`, `POST /settings/save`, `PUT /settings/change?id=`, `DELETE /settings/delete`, `GET /settings/retrive-channels` |
| Video setting | `GET|POST|PUT|DELETE /video-setting/*`                                                        |
| System        | `GET /taskManager/statistics/{allInfo,cpu,ram,swap,cpu/temperature}`                          |
| Image         | `POST /image/upload`, `GET /image/loadimage`                                                   |
| Docs          | `GET /documentazione`, `GET /swagger-ui.html`                                                  |

Responses keep the conventions the frontend expects:

```json
{ "response": "success", "message": "Operation completed" }
```

```json
{
  "content": [ { "id": 1, "videoPath": "/home/user/clip.mp4" } ],
  "page": { "size": 25, "number": 0, "totalElements": 1, "totalPages": 1 }
}
```

`GET /` redirects to `/orbis/mainMenu`. The pages keep the `/orbis/...` paths of the former
React router and call the services in-process; the language chosen in the sidebar is stored in
the `orbis-lang` cookie and also localizes the backend messages.

## Streaming pipeline

1. `POST /live/start-video-live` reads the videos attached to a channel, creates a `.txt`
   playlist and stores the FFmpeg configuration (`FfmpegVideoPlaylistStreamer`).
2. The session probes the media with `ffprobe` to obtain duration, frame rate, resolution
   and the audio/video codecs.
3. A background loop feeds `ffmpeg -re -f concat -safe 0 -i <playlist> -c copy ...` and
   writes to the RTMP URL built from the channel and platform settings.
4. `PUT /live/stop-live` (or the frontend) flips the live status, which terminates the process
   tree and closes the playlist; the row ends in the `ENDED` state.

## Repository layout

```
src/Orbis.Stream.Core     backend: API, Razor pages, data, services, FFmpeg, i18n, system info
src/Orbis.Stream.App      WPF/WebView2 shell, app icon, stylesheet and script in wwwroot
src/Orbis.Stream.Tests    xUnit suite: API end-to-end, database, paging, i18n, FFmpeg
Installer                 NSIS setup (installer.nsi) and the optional WiX MSI
```

## Tests

`dotnet test` runs the full suite, including an end-to-end test that boots the real host,
generates a short clip with FFmpeg, streams it and asserts the `ENDED` state. The FFmpeg
tests are skipped when `ffmpeg`/`ffprobe` are not available.

## Third-party components

| Component | Version | Licence | Origin |
| --------- | ------- | ------- | ------ |
| FFmpeg (bundled by the installer) | 8.1.3, win64 shared build | GPL v3 or later | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), build `autobuild-2026-09-25-15-37`, SHA-256 `1b3f0a730b1a3d780cce438d2fe091c5b8473dad2a5dbf0c2d0625029b3e70b6` |
| Microsoft.Data.Sqlite / SQLitePCLRaw | 10.0.12 | MIT | NuGet |
| Swashbuckle.AspNetCore | 9.0.1 | MIT | NuGet |
| Microsoft.Web.WebView2 | 1.0.4191.47 | redistributable | NuGet |

The installer deploys the FFmpeg licence as `ffmpeg/LICENSE-ffmpeg.txt` in the installation
folder. FFmpeg is redistributed under the terms of the GPL v3 (or later), the corresponding
source is available at <https://ffmpeg.org/download.html> and the exact build recipe at
<https://github.com/BtbN/FFmpeg-Builds>. Orbis Stream itself is MIT licensed and links to
FFmpeg as a separate process, so the two remain independent works.

## License

MIT, see [LICENSE](LICENSE).
