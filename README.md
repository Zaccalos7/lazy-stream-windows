# Orbis Stream

Desktop application that streams local video files to Twitch, YouTube, Kick, Facebook Gaming,
TikTok and any other RTMP-compatible platform.

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
| `<data>/overlays`         | overlay library of the layouts and the live wizard |
| `<data>/scene-media`      | files of the scene deck buttons                |

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
| System        | `GET /taskManager/statistics/{allInfo,cpu,ram,swap,cpu/temperature,gpu/temperature}`       |
| Image         | `POST /image/upload`, `GET /image/loadimage`                                                   |
| Scene deck    | `GET|POST /scene-buttons`, `PUT|DELETE /scene-buttons/{pkid}`, `POST /scene-buttons/media?name=`, `GET /scene-buttons/media/{name}/still`, `GET /live/{pkid}/scene`, `POST /live/{pkid}/scene/{button}`, `POST /live/{pkid}/scene/resume` |
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

### Languages

The interface ships in 20 languages: Italian, English, German, Spanish, French, Portuguese,
Russian, Chinese, Korean, Japanese, Hindi, Arabic, Romanian, Danish, Dutch, Filipino, Turkish,
Latin, Klingon and Hodor. Each one is a pair of bundles:

- `src/Orbis.Stream.Core/Ui/ui.<lang>.json`, the strings of the pages, with the keys in the order
  of the English bundle;
- `src/Orbis.Stream.Core/Messages/messages_<lang>.properties`, the messages of the backend.

A new language is registered in `UiText.Languages` (name and flag in `wwwroot/flags`) and in
`MessageCatalog.KnownLanguages`. In the `.properties` bundles every apostrophe is written twice
(`l''avvio`): the formatter reads every pattern with an apostrophe in it the way `MessageFormat`
does, and a lone one opens a quoted literal that is never printed. `UiTextTests` and
`MessageCatalogTests` fail on a missing or reordered key, a text left in English, a placeholder
lost, a language without its flag or its messages, and a lone apostrophe.

### User guide

`/orbis/manual` is the user guide of the application: the dashboard and the sidebar both open it.
It runs in every one of the 20 languages, one page per feature, and every page carries a real
screenshot of the app in that language with a callout arrow drawn on the element it talks about
(`wwwroot/manual/<lang>/<figure>.webp`). Two buttons print it or save it as a PDF through the
browser print dialog; the print stylesheet turns the guide into one page per feature.

- The text lives in `src/Orbis.Stream.Core/Ui/manual.<lang>.json`, an embedded bundle of the same
  shape for every language, read by `ManualText` with the same fallback and ordering rules as
  `UiText`; `ManualTextTests` fails on a section id, figure or step count that drifts from English
  or on a bundle left in English.
- `wwwroot/css/manual.css` holds the sheet of the page: the sticky table of contents, the
  figures, and the print rules.
- The screenshots are generated once from the running app with Puppeteer (the arrows are drawn on
  the DOM boxes of the real elements) and checked into `wwwroot/manual/`, so a release always
  ships the guide with them.

### Temperature sensors

The two temperature meters are the only counters that depend on the hardware of the machine, and
Windows has no API for either of them:

| Meter  | Source                                                                                 | Without it |
| ------ | -------------------------------------------------------------------------------------- | ---------- |
| CPU    | `MSAcpi_ThermalZoneTemperature`, then the same sensors as the performance counters      | `-1`, and the card is left out of the page |
| GPU    | `nvidia-smi`, the tool the NVIDIA driver installs, which answers from user space        | `-1`, and the card is left out of the page |

A desktop that does not publish a thermal zone named after the processor has no CPU sensor to
read, which is why the graphics card is asked as well: whichever of the two answers, the page
shows it, and the other one is not drawn at all. Sensors are read through a cache (3 s, or 30 s
when the machine has none) because a WMI query and a process launch would otherwise be paid for
on every sample of the push channel.

## Streaming pipeline

1. `POST /live/start-video-live` reads the videos attached to a channel, creates a `.txt`
   playlist and stores the FFmpeg configuration (`FfmpegVideoPlaylistStreamer`).
2. The session probes the media with `ffprobe` to obtain duration, frame rate, resolution
   and the audio/video codecs.
3. A background loop feeds `ffmpeg -re -f concat -safe 0 -i <playlist> -c copy ...` and
   writes to the RTMP URL built from the channel and platform settings.
4. `PUT /live/stop-live` (or the frontend) flips the live status, which terminates the process
   tree and closes the playlist; the row ends in the `ENDED` state.

### Scene deck

The buttons of the scene deck (up to six, the same for every live) put a video, a banner or an
image on air in place of a running live, the way Streamlabs switches to a scene. They are opened
from the row of a live and from the preview, and their keys work on those two pages only after the
panel has been opened for that live; every press asks for confirmation.

- A request is queued per live (`LiveTakeovers`): the streaming loop hands the program over at the
  position it reached, on the same connection to the platform, and brings it back from there.
- A video plays once and the live comes back by itself; an image (a still is decoded once, an
  animation loops) stays until *Resume live*, which pulses in the live row and in the preview.
- Spots go through the same queue: they wait for a video on air to end, and take the place of an
  image.
- The files are uploaded into `<data>/scene-media` and served only as frames, by name, never by
  path, for the reason the overlay library is (the server listens on every interface).

### TikTok

TikTok LIVE hands out a new stream key for every live, and it expires. A TikTok configuration
therefore keeps no key: the channel form has no key field, and the configuration stores a stand-in
(`asked-at-every-live:<channel>`, see `LiveStreamKeys`) only to satisfy the unique key of the
table. The real key is typed in at every start - in the wizard after the configuration is picked,
in the playlist dialog, and in a dialog of its own when a live is started again from its row - and
travels with that start only: the history of the live keeps it, the configuration never does. A
start that would go out with the stand-in is refused (`stream.key.required`).

The live stands up: the wizard opens the canvas at 1080 × 1920 when a TikTok configuration is
picked, the `Default TikTok` and `Default Low TikTok` presets are 1080 × 1920 and 720 × 1280, and
the platform profile turns the frame of a live that would lie down (`StreamPlatformProfile.Orient`),
so a landscape file or canvas is fitted into 9:16 with bands instead of being stretched. Delivery
is the one of YouTube: ffmpeg publishes at real time with a short head start, with AAC at 48 kHz.

### Scenes and overlays

The layouts page draws the skeleton of a live: slots, and the overlays that dress it (PNG, GIF,
WebP or WebM with transparency) taken from the overlay library. The live wizard opens a layout as
its empty slots and its overlays; the sources are dropped into the slots, and more overlays can be
laid over them from the same library or by dropping a picture on the canvas. What is on the canvas
when the live starts is saved as the scene of that live alone and never joins the layouts.

## Repository layout

```
src/Orbis.Stream.Core     backend: API, Razor pages, data, services, FFmpeg, i18n, system info
src/Orbis.Stream.App      WPF/WebView2 shell, app icon, stylesheets and script in wwwroot
src/Orbis.Stream.Tests    xUnit suite: API end-to-end, database, paging, i18n, FFmpeg
Installer                 NSIS setup (installer.nsi) and the optional WiX MSI
samples/overlays          test overlays for the layouts page (PNG, animated WebM with alpha, logo)
```

### Stylesheets

`wwwroot/css` holds one concern per file. The layout links the seven shared sheets in order
(`tokens`, `base`, `layout`, `controls`, `surfaces`, `overlays`, `feedback`); a sheet of a single
page travels with that page in its own `Styles` section (`composer.css` on the live management
and the layouts page, `scenes.css` on the live management and the preview, `preview.css`, `meters.css`, `cleanup.css`, `dashboard.css`), `manual.css` on the manual, and the
countdown page stands outside the shell and carries `tokens`, `base` and `countdown` on its own.
Colours, shadows and radii are declared once, in `tokens.css`: a sheet that needs a tone names a
property, and `StyleSheetTests` fails the build if a sheet reads a property no sheet defines or if
a page stops linking the sheet that draws it.

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
