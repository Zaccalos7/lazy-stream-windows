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

        group.MapGet("/all", (SceneService service) => Results.Ok(service.GetAll()));
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

        app.MapGet("/preview/sources", (SourceCatalogService service) => Results.Ok(service.List()));

        // One still per tile. No content is an answer, not an error: a camera that is busy in
        // another application still goes on the canvas, it just shows its icon instead.
        app.MapGet("/preview/sources/snapshot", async (
            string? kind,
            string? target,
            SourceSnapshotService service,
            CancellationToken cancellationToken) =>
        {
            if (!SourceKindExtensions.TryParse(kind, out var sourceKind))
            {
                return Results.NoContent();
            }

            var frame = await service.GrabAsync(sourceKind, target, cancellationToken).ConfigureAwait(false);
            return frame is null ? Results.NoContent() : Results.File(frame, "image/jpeg");
        });
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

        group.MapGet("/live/{pkid:int}/video", (int pkid, LivePreviewService service) =>
        {
            var file = service.FileOf(pkid);
            // The player asks for ranges to seek, so the answer has to be a file result that knows
            // about them: without this every seek would start the file over.
            return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        });

        // The light picture of a live: the JPEGs its ffmpeg writes next to the stream, pushed as
        // motion JPEG, which an <img> plays on its own. A frame goes out only when there is a new
        // one, and the answer ends with the live, so an open page costs nothing once it is over.
        group.MapGet("/live/{pkid:int}/stream", async (
            int pkid,
            HttpContext context,
            LivePreviewFrames frames,
            StreamingSessionRegistry sessions,
            CancellationToken token) =>
        {
            const string Boundary = "orbisframe";
            context.Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
            context.Response.Headers.CacheControl = "no-store";
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

            var sent = DateTime.MinValue;
            try
            {
                while (!token.IsCancellationRequested && sessions.TryGet(pkid, out _))
                {
                    var written = frames.LastWrite(pkid);
                    if (written != sent && frames.Read(pkid) is { } frame)
                    {
                        sent = written;
                        var header = System.Text.Encoding.ASCII.GetBytes(
                            $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                        await context.Response.Body.WriteAsync(header, token).ConfigureAwait(false);
                        await context.Response.Body.WriteAsync(frame, token).ConfigureAwait(false);
                        await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), token).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                    }

                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The page went away: nothing to finish.
            }
        });

        group.MapPut("/live/{pkid:int}/parameters", (
            int pkid,
            LiveParameterRequest? request,
            LivePreviewService service,
            RequestValidator validator) =>
        {
            validator.RequireLiveParameters(request);
            return AsResult(service.ApplyParameters(pkid, request!));
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
        var values = new Dictionary<string, int>(MeterKeys.Count, StringComparer.Ordinal);
        foreach (var stat in systemInfo.GetAllSystemInfo())
        {
            if (stat.Field is { } field && MeterKeys.TryGetValue(field, out var key))
            {
                values[key] = stat.Value;
            }
        }

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
