using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Minimal API translation of the five Spring controllers, keeping the same routes, the same
/// status codes and the same JSON payloads.
/// </summary>
public static class OrbisEndpoints
{
    /// <summary>How often the server samples the counters and pushes them to the open pages.</summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the browser waits before trying again after a dropped stream.</summary>
    private const int ReconnectMilliseconds = 2000;

    /// <summary>
    /// The name of the header that carries the stamp of the frame, which is what tells two frames of
    /// the same live apart. The stamp is the picture and not the file it was written to (see
    /// <see cref="LivePreviewFrames"/>), so a page that asks for the frame it is already holding is
    /// answered with the next picture ffmpeg writes, and with nothing at all while there is none.
    /// </summary>
    private const string FrameStampHeader = "X-Orbis-Frame";

    public static IEndpointRouteBuilder MapOrbisEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapLive(app);
        MapVideo(app);
        MapSettings(app);
        MapVideoSettings(app);
        MapImage(app);
        MapTaskManager(app);
        MapPreview(app);
        MapScene(app);
        MapSceneButtons(app);
        MapUpdates(app);
        return app;
    }

    /// <summary>
    /// The canvases and the sources that can go on them. The catalog is a separate route from the
    /// scenes because the page asks for it on every open, while the scenes are only fetched when
    /// one is being edited.
    /// </summary>
    private static void MapScene(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scene");

        group.MapGet("/layouts", (SceneService service) => Results.Ok(service.GetLayouts()));
        group.MapGet("/{pkid:long}", (long pkid, SceneService service) => Results.Ok(service.GetOne(pkid)));
        group.MapPost("/save", (SceneRequest? request, SceneService service) =>
        {
            if (request is null)
            {
                throw new RequestValidationException(new Dictionary<string, string>
                {
                    ["body"] = "input.not.valid"
                });
            }

            var (response, pkid) = service.Save(request);
            return Results.Json(
                new SceneSavedResponse(response.Body.Response, response.Body.Message, pkid),
                statusCode: response.StatusCode);
        });
        group.MapDelete("/{pkid:long}", (long pkid, SceneService service) => AsResult(service.Delete(pkid)));

        MapOverlays(group);

        app.MapGet("/preview/sources", (SourceCatalogService service) => Results.Ok(service.List()));

        // One still per tile. No content is an answer, not an error: a camera that is busy in
        // another application still goes on the canvas, it just shows its icon instead.
        app.MapGet("/preview/sources/snapshot", async (
            HttpContext context,
            string? kind,
            string? target,
            SourceSnapshotService service,
            CancellationToken cancellationToken) =>
        {
            // The URL of a source never changes, so a still that could not be grabbed would be
            // the answer for ever: 204 is cacheable, and the tile of a scene saved yesterday would
            // keep showing its icon even with the source back. A still is a moment, asked again.
            context.Response.Headers.CacheControl = "no-store";

            if (!SourceKindExtensions.TryParse(kind, out var sourceKind))
            {
                return Results.NoContent();
            }

            var frame = await service.GrabAsync(sourceKind, target, cancellationToken).ConfigureAwait(false);
            return frame is null ? Results.NoContent() : Results.File(frame, "image/jpeg");
        });

        // A file has just been laid on a tile: if it is too heavy to decode in real time, its light
        // copy is made now, in the background, so it is ready long before the live is.
        app.MapPost("/preview/sources/prepare", (string? target, MediaProxyService proxies) =>
        {
            if (!SourceSnapshotService.IsSnapshottable(SourceKind.File, target))
            {
                return Results.NoContent();
            }

            proxies.Prepare(target!);
            return Results.Accepted();
        });

        // The real resolution of a video file, read by ffprobe the moment it lands on the canvas:
        // the still is scaled down, so it cannot say how many pixels the file has, and that is the
        // most the composition can be streamed at without upscaling it.
        app.MapGet("/preview/sources/probe", async (
            string? target,
            FfmpegProbe probe,
            CancellationToken cancellationToken) =>
        {
            if (!SourceSnapshotService.IsSnapshottable(SourceKind.File, target))
            {
                return Results.NoContent();
            }

            try
            {
                var media = await probe
                    .ProbeAsync(StreamingService.NormalizeUserPath(target!), cancellationToken)
                    .ConfigureAwait(false);
                return media.Width > 0 && media.Height > 0
                    ? Results.Ok(new { width = media.Width, height = media.Height, durationSeconds = media.DurationSeconds })
                    : Results.NoContent();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Results.NoContent();
            }
        });
    }

    /// <summary>
    /// The overlay library of the layouts (see <see cref="OverlayLibrary"/>). The pictures come in
    /// as uploads and go out by their name in the library, never by a path: these routes cannot
    /// reach a file the user did not put there.
    /// </summary>
    private static void MapOverlays(RouteGroupBuilder group)
    {
        group.MapGet("/overlays", (OverlayLibrary library) => Results.Ok(library.List()));

        group.MapPost("/overlays", async (
            HttpRequest request,
            OverlayLibrary library,
            RequestValidator validator,
            CancellationToken cancellationToken) =>
        {
            // Kestrel stops a body at 30 MB unless told otherwise, and an animated overlay can be
            // bigger. The form around the file is a few hundred bytes more than the file itself.
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = OverlayLibrary.MaxBytes + 1024 * 1024;
            }

            if (!request.HasFormContentType)
            {
                validator.RequireOverlay(null);
            }

            var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            var file = form.Files["file"];
            validator.RequireOverlay(file);

            await using var content = file!.OpenReadStream();
            var entry = await library.AddAsync(file.FileName, content, cancellationToken).ConfigureAwait(false);
            return Results.Json(entry, statusCode: StatusCodes.Status201Created);
        });

        // The name of a file of the library is its content, so a name always answers with the same
        // picture: the browser keeps it, and a layout opened again does not download it again.
        group.MapGet("/overlays/{name}", (string name, HttpContext context, OverlayLibrary library) =>
        {
            if (library.Find(name) is not { } file)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        });

        group.MapGet("/overlays/{name}/still", async (
            string name,
            OverlayLibrary library,
            CancellationToken cancellationToken) =>
        {
            var still = await library.StillAsync(name, cancellationToken).ConfigureAwait(false);
            return still is null ? Results.NoContent() : Results.File(still, "image/png");
        });

        group.MapDelete("/overlays/{name}", (string name, OverlayLibrary library) => AsResult(library.Delete(name)));
    }

    private static void MapLive(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/live");

        group.MapPost("/start-scene-live", (
            StartSceneLiveRequest? request,
            StreamingService service,
            RequestValidator validator) =>
        {
            validator.RequireStartSceneLive(request);
            return AsResult(service.StartSceneLive(request!));
        });

        group.MapPost("/start-live", (
            StartLiveRequest? request,
            StreamingService service,
            RequestValidator validator) =>
        {
            validator.RequireStartLive(request);
            return AsResult(service.StartLive(request!));
        });

        group.MapPost("/start-video-live", (
            VideoRequest? request,
            StreamingService service,
            RequestValidator validator) =>
        {
            validator.RequireVideo(request);
            return AsResult(service.StartVideo(request!));
        });

        group.MapPut("/stop-live", (
            int videoLivePkid,
            StreamingService service,
            ResponseFactory responses,
            Localizer localizer) =>
        {
            service.StopVideoStreamingByPkid(videoLivePkid);
            localizer.PrintMessage("live.stopped");
            return AsResult(responses.Build("live.stopped", StatusCodes.Status200OK));
        });

        // The scene deck of a running live: what is on air in place of its program, a button to put
        // on air now, and the way back to the program. Any row of the live answers for all of it.
        group.MapGet("/{pkid:int}/scene", (int pkid, StreamingService service) => Results.Ok(service.SceneStateOf(pkid)));

        group.MapPost("/{pkid:int}/scene/resume", (int pkid, StreamingService service) => AsResult(service.ResumeProgram(pkid)));

        group.MapPost("/{pkid:int}/scene/{button:long}", (int pkid, long button, StreamingService service) =>
            AsResult(service.PlaySceneButton(pkid, button)));
    }

    /// <summary>
    /// The buttons of the scene deck and the files they carry (see <see cref="SceneButtonService"/>).
    /// A file comes in as the body of the request, the way the browser hands it over, and goes out
    /// as a frame of it, by its name in the folder: never by a path.
    /// </summary>
    private static void MapSceneButtons(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scene-buttons");

        group.MapGet("", (SceneButtonService service) => Results.Ok(service.List()));

        group.MapPost("", (SceneButtonRequest? request, SceneButtonService service) =>
            Results.Json(service.Create(request), statusCode: StatusCodes.Status201Created));

        group.MapPut("/{pkid:long}", (long pkid, SceneButtonRequest? request, SceneButtonService service) =>
            Results.Ok(service.Update(pkid, request)));

        group.MapDelete("/{pkid:long}", (long pkid, SceneButtonService service) => AsResult(service.Delete(pkid)));

        // The file is the whole body, and its name is in the query: a clip is too big for a form
        // to be worth its parsing, and Kestrel would stop it at 30 MB unless told otherwise.
        group.MapPost("/media", async (
            HttpRequest request,
            string? name,
            SceneButtonService service,
            CancellationToken cancellationToken) =>
        {
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = SceneButtonService.MaxBytes + 1024 * 1024;
            }

            var media = await service.AddMediaAsync(name, request.Body, cancellationToken).ConfigureAwait(false);
            return Results.Json(media, statusCode: StatusCodes.Status201Created);
        });

        // The name of a file is its content, so a frame of it is the same frame for ever.
        group.MapGet("/media/{name}/still", async (
            string name,
            HttpContext context,
            SceneButtonService service,
            CancellationToken cancellationToken) =>
        {
            if (await service.StillAsync(name, cancellationToken).ConfigureAwait(false) is not { } still)
            {
                return Results.NoContent();
            }

            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(still.Bytes, still.ContentType);
        });
    }

    private static void MapVideo(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/video");

        group.MapGet("/getPage", (HttpContext context, VideoService service) => service.GetVideoList(
            PageRequest.Parse(context.Request.Query, 20, "pkid", true)));

        group.MapGet("/getAllVideo", (HttpContext context, VideoService service) =>
        {
            var query = context.Request.Query;
            var filters = Filters(query, "page", "size", "sort", "platform", "lang");
            var page = PageRequest.Parse(query, 4, "startDateLive", true);
            return Results.Ok(service.GetAllVideoList(filters, page));
        });

        group.MapGet("/getAllChannelWithVideoLive", (VideoService service) => Results.Ok(service.GetAllChannelOnline()));

        group.MapPut("/unlockVideo", (int videoKey, VideoService service) => AsResult(service.UnlockVideo(videoKey)));

        // Live History endpoints
        group.MapDelete("/live-history/{pkid:long}", (long pkid, VideoService service) => AsResult(service.DeleteLiveHistory(pkid)));
        group.MapDelete("/live-history/older-than/{months:int}", (int months, VideoService service) => AsResult(service.DeleteOldLiveHistory(months)));

        // The cleanup the page queues: it answers at once and the page asks how far it has got,
        // because deleting a hundred lives takes long enough to be worth watching.
        group.MapPost("/live-history/cleanup", (int months, LiveHistoryCleanupService cleanup) => cleanup.TryStart(months)
            ? Results.Ok(cleanup.Progress)
            : Results.Json(cleanup.Progress, statusCode: StatusCodes.Status409Conflict));
        group.MapGet("/live-history/cleanup", (LiveHistoryCleanupService cleanup) => Results.Ok(cleanup.Progress));
    }

    private static void MapSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings");

        group.MapPost("/save", (SettingRequest? request, SettingService service, RequestValidator validator) =>
        {
            validator.RequireSetting(request);
            return AsResult(service.AddNewConfiguration(request!));
        });

        group.MapPut("/change", (int id, SettingRequest? request, SettingService service, RequestValidator validator) =>
        {
            validator.RequireSetting(request);
            return AsResult(service.ModifySetting(id, request!));
        });

        group.MapGet("/retrive", (HttpContext context, SettingService service) =>
            Results.Ok(service.RetrieveSettings(Filters(context.Request.Query, "lang"))));

        group.MapGet("/retrive-channels", (HttpContext context, SettingService service) =>
            Results.Ok(service.RetrieveChannel(Filters(context.Request.Query, "lang"))));

        group.MapGet("/retrive-directories-path", (HttpContext context, SettingService service) =>
            Results.Ok(service.RetrieveDirectoriesSettingsPath(Filters(context.Request.Query, "lang"))));

        group.MapDelete("/delete", (int id, SettingService service) => AsResult(service.DeleteAStreamingSetting(id)));
    }

    private static void MapVideoSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/video-setting");

        group.MapGet("/getVideoSettings", (HttpContext context, VideoSettingService service) =>
            Results.Ok(service.GetAllVideoSettings(Filters(context.Request.Query, "lang"))));

        group.MapPost("/save", (VideoSettingsRequest? request, VideoSettingService service) =>
            AsResult(service.SaveSettingsVideo(request!)));

        group.MapPost("/link", (int videoPkidToLink, VideoSettingsRequest? request, VideoSettingService service) =>
            AsResult(service.LinkAndSaveSettingsVideo(request!, videoPkidToLink)));

        group.MapPut("/edit", (int id, VideoSettingsRequest? request, VideoSettingService service) =>
            AsResult(service.EditSettingsVideo(request!, id)));

        group.MapDelete("/delete", (int id, VideoSettingService service) => AsResult(service.DeleteVideoSetting(id)));
    }

    private static void MapImage(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/image");

        group.MapPost("/upload", async (
            HttpRequest request,
            ImageService service,
            RequestValidator validator,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                throw new BadHttpRequestException("Expected a multipart/form-data request with the 'image' field.");
            }

            var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            var image = form.Files["image"];
            validator.RequireImage(image);

            return AsResult(service.SaveImage(image!));
        });

        group.MapGet("/loadimage", (ImageService service) =>
        {
            var image = service.LoadImage();
            return Results.File(image.Path, image.ContentType);
        });
    }

    private static void MapTaskManager(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/taskManager/statistics");

        group.MapGet("/allInfo", (SystemInfoService service) => Results.Ok(service.GetAllSystemInfo()));
        group.MapGet("/cpu", (SystemInfoService service) => Results.Ok(service.GetCpuPercent()));
        group.MapGet("/ram", (SystemInfoService service) => Results.Ok(service.GetRamPercent()));
        group.MapGet("/swap", (SystemInfoService service) => Results.Ok(service.GetSwapPercent()));
        group.MapGet("/cpu/temperature", (SystemInfoService service) => Results.Ok(service.GetCpuTemperature()));
        group.MapGet("/gpu/temperature", (SystemInfoService service) => Results.Ok(service.GetGpuTemperature()));
    }

    /// <summary>
    /// The preview API: where the page reads the state of the live it is watching, the file behind
    /// the player, and the endpoint that changes the parameters of a running transcode.
    /// </summary>
    private static void MapPreview(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/preview");

        group.MapGet("/live", (LivePreviewService service, HttpRequest request) =>
            Results.Json(service.Snapshot(Watched(request))));

        // The player of the platform a live is on, for a page that would rather show the live as
        // the viewers see it than as the encoder sees it. It is asked for once the page is open and
        // not while it is drawn, because on YouTube the address of the player is not known before
        // the platform has been asked which video is on air, and that takes longer than a page
        // should wait to be drawn. There is nothing to hand back when the live is over, when the
        // platform is one this application has no player for, or when the channel is not on air,
        // and a page that is told so keeps the local picture.
        group.MapGet("/live/embed", async (
            LivePreviewService service,
            HttpRequest request,
            HttpContext context) =>
        {
            var found = await service.EmbedAsync(
                Watched(request),
                context.Request.Host.Host,
                context.RequestAborted);

            // There is no address to play, but the reason travels with the answer: a channel that is
            // off air and a platform that could not be reached are different things to tell a page,
            // and the page is what repeats the answer for as long as it stays open.
            return Results.Ok(new { embed = found.Embed, reason = found.Reason });
        });

        // The user closed the YouTube warning of a live: it stays closed until the live is found on
        // air. The live is its history, so the warning is closed for every video of it at once.
        group.MapPost("/live/{pkid:int}/air/dismiss", (int pkid, VideoRepository videos, YouTubeAirWatch air) =>
        {
            if (videos.FindByPkid(pkid)?.VideoLiveHistoryId is not { } history)
            {
                return Results.NotFound();
            }

            air.Dismiss(history);
            return Results.NoContent();
        });

        group.MapGet("/live/{pkid:int}/video", (int pkid, LivePreviewService service) =>
        {
            var file = service.FileOf(pkid);
            // The player asks for ranges to seek, so the answer has to be a file result that knows
            // about them: without this every seek would start the file over.
            return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        });

        // One frame of the light picture, as a whole JPEG. This is what the preview page draws, and
        // it is the endpoint the picture is judged on: a page that keeps asking for the newest
        // frame gets the newest frame, and a frame it did not get in time is simply gone instead of
        // arriving late and dragging the picture behind the live. The answer is short (the picture
        // is 640 pixels wide), and the stamp in the header lets a page that already has this exact
        // frame be answered without the bytes at all.
        //
        // A page that says which frame it holds can also say how long it is willing to wait for the
        // next one (?wait=): the request is then held open until ffmpeg has written a picture the
        // page has not seen, which is what keeps the page and the encoder on one clock. Left out,
        // the answer is immediate, which is what a page that would rather come back on its own
        // timer asks for.
        group.MapGet("/live/{pkid:int}/frame", async (
            int pkid,
            HttpContext context,
            LivePreviewFrames frames,
            StreamingSessionRegistry sessions) =>
        {
            // A live that is over has no frame: the page covers the picture rather than leaving the
            // last one standing as if it were still on air.
            if (!sessions.TryGet(pkid, out _))
            {
                return Results.NotFound();
            }

            var held = StampOf(context.Request);
            var frame = await frames.NextAsync(pkid, held, WaitOf(context.Request), context.RequestAborted);

            if (frame is null)
            {
                // Nothing new within the time the page was willing to give: a page that is ahead of
                // the live is told so without the bytes, and a live that has written no frame at all
                // is not there yet. A live that ended while the page waited has no session any more,
                // and the page has to hear that rather than keep its picture waiting.
                return sessions.TryGet(pkid, out _)
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers[FrameStampHeader] = frame.Stamp;

            return Results.File(frame.Bytes, "image/jpeg");
        });

        // The last third of the preview: the encoder settings of a live that is already running.
        group.MapPut("/live/{pkid:int}/parameters", (
            int pkid,
            LiveParameterRequest? request,
            LivePreviewService service,
            RequestValidator validator) =>
        {
            validator.RequireLiveParameters(request);
            return AsResult(service.ApplyParameters(pkid, request!));
        });

        // The level of one source of a canvas in the mix of the live.
        group.MapPut("/live/{pkid:int}/volume", (
            int pkid,
            LiveVolumeRequest? request,
            LivePreviewService service,
            RequestValidator validator) =>
        {
            validator.RequireLiveVolume(request);
            return AsResult(service.ApplyVolume(pkid, request!));
        });
    }

    /// <summary>The live a preview page is following, when it asked for one that is still running.</summary>
    private static int? Watched(HttpRequest request)
    {
        var raw = request.Query["live"].FirstOrDefault();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pkid) && pkid > 0
            ? pkid
            : null;
    }

    /// <summary>
    /// The frame a page already holds, which is the <c>If-None-Match</c> stamp of the answer it was
    /// given: the server looks for the next one instead of sending that picture again.
    /// </summary>
    private static string? StampOf(HttpRequest request)
    {
        foreach (var value in request.Headers.IfNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A page that quotes the stamp back, the way a browser quotes an ETag, is understood too.
            var stamp = value.Trim();
            if (stamp.Length > 1 && stamp[0] == '"' && stamp[^1] == '"')
            {
                stamp = stamp[1..^1];
            }

            if (stamp.Length > 0)
            {
                return stamp;
            }
        }

        return null;
    }

    /// <summary>
    /// How long a page is willing to be left waiting for the frame it does not have yet. No more than
    /// <see cref="LivePreviewFrames.MaximumWait"/>, and nothing at all when the page did not ask.
    /// </summary>
    private static TimeSpan WaitOf(HttpRequest request)
    {
        var raw = request.Query["wait"].FirstOrDefault();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, LivePreviewFrames.MaximumWait.TotalMilliseconds));
    }

    /// <summary>
    /// The push channel the pages listen to, so that they ask for what changed instead of asking
    /// every few seconds. Three kinds of message: <c>rows</c> when a live row moved (the page
    /// redoes its partial), <c>stats</c> with the counters themselves and <c>live</c> with the
    /// state of the live the page is watching (the page paints it, with no request at all). The
    /// sampling stays on the server, where the value comes from anyway: sampling is unavoidable,
    /// being asked again by every window is not.
    /// </summary>
    private static void MapUpdates(IEndpointRouteBuilder app)
    {
        app.MapGet("/updates", async (
            HttpContext context,
            LiveChangeNotifier notifier,
            SystemInfoService systemInfo,
            LivePreviewService preview,
            CancellationToken token) =>
        {
            var watchRows = Watches(context.Request, "rows");
            var watchStats = Watches(context.Request, "stats");
            var watchLive = Watches(context.Request, "live");
            if (!watchRows && !watchStats && !watchLive)
            {
                return Results.Empty;
            }

            var response = context.Response;
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache, no-store";
            response.Headers["X-Accel-Buffering"] = "no";

            // Where the client stands: the change the page drew, or where a reconnect resumes from.
            var version = LastVersion(context.Request);

            async Task WriteAsync(string name, string data, bool numbered = false)
            {
                // Only a change carries the id: it is what the browser sends back as Last-Event-ID
                // after a reconnect, so a resumed stream repeats nothing the page already has. A
                // counter would put a number there that means nothing, and lose the change number.
                var head = numbered ? $"id: {version.ToString(CultureInfo.InvariantCulture)}\n" : string.Empty;
                await response.WriteAsync($"{head}event: {name}\ndata: {data}\n\n", token).ConfigureAwait(false);
                await response.Body.FlushAsync(token).ConfigureAwait(false);
            }

            // One timer for both sampled messages: they are both read once a second, and a page
            // watching one of them is not paying for a tick that paints nothing.
            using var sampler = watchStats || watchLive ? new PeriodicTimer(StatsInterval) : null;
            var watched = watchLive ? Watched(context.Request) : null;

            try
            {
                // A hello, which is the only thing the page is told before something happens: it
                // says how long to wait before trying again after a dropped stream, and it is what
                // makes the answer of the browser come now instead of at the first message.
                await response.WriteAsync($"retry: {ReconnectMilliseconds}\n\n", token).ConfigureAwait(false);
                await response.Body.FlushAsync(token).ConfigureAwait(false);

                // The page opens with empty meters, so the first sample goes out now instead of at
                // the first tick: waiting for the timer would leave them on the loading a second more.
                if (watchStats)
                {
                    await WriteAsync("stats", StatsJson(systemInfo)).ConfigureAwait(false);
                }

                if (watchLive)
                {
                    await WriteAsync("live", LiveJson(preview, watched)).ConfigureAwait(false);
                }

                // Every source is awaited together: a change must not hold back the samples, and a
                // tick must not be waited for by a page that does not want it. A source nobody
                // watches stays pending until the client goes away.
                var rows = watchRows ? notifier.WaitAsync(version, token) : NeverRows(version, token);
                var sample = sampler is not null ? sampler.WaitForNextTickAsync(token).AsTask() : NeverStats(token);

                while (true)
                {
                    var first = await Task.WhenAny(rows, sample).ConfigureAwait(false);

                    if (first == rows)
                    {
                        version = await rows.ConfigureAwait(false);
                        if (watchRows)
                        {
                            await WriteAsync("rows", version.ToString(CultureInfo.InvariantCulture), numbered: true).ConfigureAwait(false);
                        }

                        rows = watchRows ? notifier.WaitAsync(version, token) : NeverRows(version, token);
                    }
                    else
                    {
                        if (watchStats)
                        {
                            await WriteAsync("stats", StatsJson(systemInfo)).ConfigureAwait(false);
                        }

                        if (watchLive)
                        {
                            await WriteAsync("live", LiveJson(preview, watched)).ConfigureAwait(false);
                        }

                        sample = sampler is not null ? sampler.WaitForNextTickAsync(token).AsTask() : NeverStats(token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The window was closed or the page was left: the browser reconnects on its own and
                // resumes from the last change it saw, so this is the ordinary end of a stream.
            }

            return Results.Empty;
        });
    }

    /// <summary>
    /// A source nobody asked to hear: it stays pending until the client goes away, so the loop
    /// waits on the other one without spinning.
    /// </summary>
    private static Task Never(CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);

    private static async Task<long> NeverRows(long version, CancellationToken token)
    {
        await Never(token).ConfigureAwait(false);
        return version;
    }

    private static async Task<bool> NeverStats(CancellationToken token)
    {
        await Never(token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A closed connection is not an error: the browser reconnects and resumes from the last change
    /// it saw, which travels in the Last-Event-ID header.
    /// </summary>
    private static long LastVersion(HttpRequest request)
    {
        var raw = request.Headers["Last-Event-ID"].FirstOrDefault() ?? request.Query["since"].FirstOrDefault();

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : 0;
    }

    /// <summary>What the page wants to hear about, as a comma separated <c>watch</c> list.</summary>
    private static bool Watches(HttpRequest request, string what) =>
        request.Query["watch"]
            .FirstOrDefault()?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(what, StringComparer.OrdinalIgnoreCase) is true;

    /// <summary>The API names of the counters (the Java contract) paired with the meters of the page.</summary>
    private static readonly Dictionary<string, string> MeterKeys = Enum.GetValues<SystemInfoField>()
        .ToDictionary(field => field.ToInfoName(), field => field.ToMeterKey(), StringComparer.Ordinal);

    /// <summary>
    /// The counters keyed by the meter that shows them: <c>cpu</c>, <c>ram</c>, <c>swap</c> and
    /// <c>cpu_temperature</c>. The service answers them under the API names ("CPU", "TEMPERATURA
    /// CPU"), which stay as they are because the REST endpoints return them.
    /// </summary>
    private static string StatsJson(SystemInfoService systemInfo)
    {
        var values = new Dictionary<string, object>(MeterKeys.Count + 1, StringComparer.Ordinal);
        foreach (var stat in systemInfo.GetAllSystemInfo())
        {
            if (stat.Field is { } field && MeterKeys.TryGetValue(field, out var key))
            {
                values[key] = stat.Value;
            }
        }

        values["ffmpeg_processes"] = systemInfo.GetFfmpegProcessStats();

        return JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// The state of a live as the preview page wants it: the same camel case the rest of the API
    /// answers in, so the page reads one shape whether the first sample came from the endpoint or
    /// from the stream.
    /// </summary>
    private static string LiveJson(LivePreviewService preview, int? watched) =>
        JsonSerializer.Serialize(preview.Snapshot(watched), PreviewJson);

    private static readonly JsonSerializerOptions PreviewJson = new(JsonSerializerDefaults.Web);

    /// <summary>Writes the <c>{ response, message }</c> envelope with the Spring status code.</summary>
    private static IResult AsResult(MessageResponse response) =>
        Results.Json(response.Body, statusCode: response.StatusCode, contentType: "application/json");

    /// <summary>
    /// Every request parameter is an equality filter; the paging parameters are not entity
    /// properties and <c>platform</c> is a frontend-only filter that the Java version rejected.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Filters(IQueryCollection query, params string[] excluded)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            if (excluded.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            filters[key] = values.ToString();
        }

        return filters;
    }
}
