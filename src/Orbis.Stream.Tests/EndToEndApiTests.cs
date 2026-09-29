using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.SystemInfo;

namespace Orbis.Stream.Tests;

/// <summary>Boots the production host on a free port with a temporary data directory.</summary>
public sealed class TestHostRunner : IAsyncDisposable
{
    private WebApplication? _host;

    private TestHostRunner(WebApplication host, string dataDirectory, int port, string webRootPath)
    {
        _host = host;
        DataDirectory = dataDirectory;
        Port = port;
        WebRootPath = webRootPath;
        Client = CreateClient();
        NoRedirectClient = CreateClient(allowAutoRedirect: false);
    }

    public string DataDirectory { get; }

    public int Port { get; }

    public string WebRootPath { get; }

    public HttpClient Client { get; }

    public HttpClient NoRedirectClient { get; }

    /// <summary>The services of the running host, so that a test can ring the bell as the app does.</summary>
    public IServiceProvider Services => _host!.Services;

    public static TestHostRunner Start(string? ffmpegPath = null, string? ffprobePath = null)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "orbis-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var port = FreePort();
        var webRootPath = Path.Combine(AppContext.BaseDirectory, "WebRoot");
        Directory.CreateDirectory(webRootPath);
        File.WriteAllText(
            Path.Combine(webRootPath, "index.html"),
            "<!doctype html><html><head><base href=\"/orbis/\"></head><body>orbis</body></html>");

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [OrbisRuntimeOptions.FfmpegEnvironmentVariable] = ffmpegPath ?? "ffmpeg-not-installed",
            [OrbisRuntimeOptions.FfprobeEnvironmentVariable] = ffprobePath ?? "ffprobe-not-installed"
        };

        var options = OrbisRuntimeOptions.Resolve(
            [$"--port={port}", $"--data-dir={dataDirectory}", $"--web-root={webRootPath}"],
            environment);

        options.EnsureDirectories();
        var host = OrbisHost.Create(options);
        host.StartAsync().GetAwaiter().GetResult();
        return new TestHostRunner(host, dataDirectory, port, webRootPath);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        NoRedirectClient.Dispose();
        if (_host is not null)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(shutdown.Token);
            await _host.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temporary directory.
        }
    }

    private HttpClient CreateClient(bool allowAutoRedirect = true) => new(new HttpClientHandler
    {
        AllowAutoRedirect = allowAutoRedirect
    })
    {
        BaseAddress = new Uri($"http://localhost:{Port}"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Boots the real in-process host (same <see cref="OrbisHost"/> used by the WPF shell) on a free
/// port with a temporary data directory and exercises the HTTP contract the React build relies on.
/// </summary>
public sealed class ApplicationFixture : IAsyncLifetime
{
    private TestHostRunner _runner = null!;

    public string DataDirectory => _runner.DataDirectory;

    public int Port => _runner.Port;

    public HttpClient Client => _runner.Client;

    public HttpClient NoRedirectClient => _runner.NoRedirectClient;

    public IServiceProvider Services => _runner.Services;

    public string WebRootPath => _runner.WebRootPath;

    public Task InitializeAsync()
    {
        _runner = TestHostRunner.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _runner.DisposeAsync().AsTask();
}

public sealed class EndToEndApiTests : IClassFixture<ApplicationFixture>
{
    private const int TwitchVideoSettingId = 1;

    private readonly ApplicationFixture _fixture;

    public EndToEndApiTests(ApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheDefaultSettingsAreNamedAfterTheirPlatform()
    {
        // Two default settings both called "test" are impossible to tell apart in the wizard and in
        // the settings, so the name is the platform they are for.
        using var response = await _fixture.Client.GetAsync("/video-setting/getVideoSettings");
        var settings = await response.Content.ReadFromJsonAsync<JsonElement>();

        var defaults = settings.EnumerateArray()
            .Where(setting => setting.GetProperty("isDefaultConfiguration").GetBoolean())
            .ToDictionary(
                setting => setting.GetProperty("defaultPlatformConfiguration").GetString() ?? string.Empty,
                setting => setting.GetProperty("title").GetString() ?? string.Empty);

        Assert.Equal("Default Twitch", defaults["Twitch"]);
        Assert.Equal("Default Youtube", defaults["Youtube"]);
        Assert.All(defaults.Values, title => Assert.False(string.Equals(title, "test", StringComparison.OrdinalIgnoreCase)));

        // And the name is what the wizard shows, next to the platform.
        using var page = await _fixture.Client.GetAsync("/orbis/mainLive?start=1");
        var body = await page.Content.ReadAsStringAsync();

        Assert.Contains("Default Twitch", body, StringComparison.Ordinal);
        Assert.Contains("Default Youtube", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_RedirectsToTheClientRouter()
    {
        using var response = await _fixture.NoRedirectClient.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/orbis/mainMenu", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task StaticFiles_AreAlwaysRevalidated()
    {
        var stylesheet = Path.Combine(_fixture.WebRootPath, "css", "fluent.css");
        Directory.CreateDirectory(Path.GetDirectoryName(stylesheet)!);
        await File.WriteAllTextAsync(stylesheet, "body{}");

        using var response = await _fixture.Client.GetAsync("/css/fluent.css");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoCache);
    }

    [Theory]
    [InlineData("/orbis/mainMenu")]
    [InlineData("/orbis/mainLive")]
    [InlineData("/orbis/mainLive?liveStatus=LIVE&p=3")]
    [InlineData("/orbis/mainLive?handler=Rows")]
    [InlineData("/orbis/mainLive?link=1")]
    [InlineData("/orbis/mainLiveHistory")]
    [InlineData("/orbis/mainSetting")]
    [InlineData("/orbis/mainVideoSetting")]
    [InlineData("/orbis/mainVideoSetting?edit=new&preset=Twitch")]
    [InlineData("/orbis/mainChannelSetting")]
    [InlineData("/orbis/mainChannelSetting?edit=new&platform=twitch&active=true")]
    [InlineData("/orbis/mainTaskManager")]
    [InlineData("/orbis/mainTaskManager?handler=Stats")]
    [InlineData("/orbis/countdown")]
    public async Task Pages_RenderOnTheServer(string path)
    {
        // mainLive used to crash in the React build when no video was live (x?.map on a 404 body).
        using var response = await _fixture.Client.GetAsync(path);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LanguageSelector_StoresTheChoiceAndTranslatesThePages()
    {
        using var choose = new HttpRequestMessage(HttpMethod.Get, "/orbis/lang/it");
        choose.Headers.Referrer = new Uri(_fixture.Client.BaseAddress!, "/orbis/mainLive?p=1");
        using var chosen = await _fixture.NoRedirectClient.SendAsync(choose);

        Assert.Equal(HttpStatusCode.Redirect, chosen.StatusCode);
        Assert.Equal("/orbis/mainLive?p=1", chosen.Headers.Location?.OriginalString);
        var cookie = chosen.Headers.GetValues("Set-Cookie").Single().Split(";")[0];

        using var page = new HttpRequestMessage(HttpMethod.Get, "/orbis/mainMenu");
        page.Headers.Add("Cookie", cookie);
        using var italian = await _fixture.NoRedirectClient.SendAsync(page);
        var html = await italian.Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"it\">", html, StringComparison.Ordinal);
        Assert.Contains("Gestione Live", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSelector_NeverRedirectsOutsideTheApplication()
    {
        using var choose = new HttpRequestMessage(HttpMethod.Get, "/orbis/lang/xx");
        choose.Headers.Referrer = new Uri("http://evil.example//orbis/x");
        using var response = await _fixture.NoRedirectClient.SendAsync(choose);

        Assert.Equal("/orbis/mainMenu", response.Headers.Location?.OriginalString);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task UnknownPaths_AreNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/orbis/liveSettings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_IsExposedUnderTheDocumentedPaths()
    {
        using var document = await _fixture.Client.GetAsync("/documentazione");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Equal("application/json", document.Content.Headers.ContentType?.MediaType);

        var json = await document.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(json);
        Assert.StartsWith("3.0", json!.RootElement.GetProperty("openapi").GetString()!, StringComparison.Ordinal);
        Assert.True(json.RootElement.TryGetProperty("paths", out var paths));
        Assert.True(paths.TryGetProperty("/live/start-live", out _));

        using var redirect = await _fixture.NoRedirectClient.GetAsync(
            "/swagger-ui.html",
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/swagger-ui/index.html", redirect.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task GetPage_ReturnsTheSpringPageEnvelope()
    {
        using var response = await _fixture.Client.GetAsync("/video/getPage?size=5&page=0&sort=pkid,desc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, page.GetProperty("content").ValueKind);
        var metadata = page.GetProperty("page");
        var total = metadata.GetProperty("totalElements").GetInt64();
        Assert.Equal(5, metadata.GetProperty("size").GetInt32());
        Assert.Equal(0, metadata.GetProperty("number").GetInt32());
        Assert.True(total >= 0);
        Assert.Equal(total == 0, metadata.GetProperty("empty").GetBoolean());
        Assert.True(metadata.GetProperty("first").GetBoolean());
        Assert.Equal(page.GetProperty("content").GetArrayLength(), metadata.GetProperty("numberOfElements").GetInt32());
        Assert.Equal(
            total == 0 ? 0 : (int)Math.Ceiling(total / 5d),
            metadata.GetProperty("totalPages").GetInt32());
        Assert.Equal("DESC", metadata.GetProperty("sort").GetProperty("sort")[0].GetProperty("direction").GetString());
    }

    [Fact]
    public async Task GetAllVideo_RejectsAnUnknownFilterProperty()
    {
        using var response = await _fixture.Client.GetAsync("/video/getAllVideo?thisIsNotAColumn=1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task GetAllVideo_IgnoresThePagingSortAndLanguageParameters()
    {
        using var response = await _fixture.Client.GetAsync(
            "/video/getAllVideo?size=2&page=0&sort=startDateLive,desc&lang=it&platform=Twitch");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Settings_AreCreatedListedAndDeleted()
    {
        var payload = new
        {
            streamUrl = "rtmp://ingest.example/live",
            streamKey = "stream-key",
            platformStreamName = "channel-e2e",
            description = "created by the test suite",
            isActive = true,
            channelName = "channel-e2e",
            videoFolder = _fixture.DataDirectory
        };

        using var invalid = await _fixture.Client.PostAsJsonAsync("/settings/save", new { streamUrl = "rtmp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        // The Java ExceptionsHandler answered the Bean Validation failures with a plain
        // field -> localized message map.
        var validation = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(validation.GetProperty("streamKey").GetString()!.Length > 0);
        Assert.True(validation.GetProperty("channelName").GetString()!.Length > 0);

        using var created = await _fixture.Client.PostAsJsonAsync("/settings/save", payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("success", createdBody.GetProperty("response").GetString());

        using var duplicated = await _fixture.Client.PostAsJsonAsync("/settings/save", payload);
        Assert.Equal(HttpStatusCode.Conflict, duplicated.StatusCode);

        using var list = await _fixture.Client.GetAsync("/settings/retrive?channelName=channel-e2e");
        var settings = await list.Content.ReadFromJsonAsync<JsonElement>();
        var saved = Assert.Single(settings.EnumerateArray());
        Assert.Equal("rtmp://ingest.example/live", saved.GetProperty("streamUrl").GetString());
        var id = saved.GetProperty("id").GetInt32();

        using var changed = await _fixture.Client.PutAsJsonAsync(
            $"/settings/change?id={id}",
            payload with { description = "updated" });
        // modifySetting answered HttpStatus.ACCEPTED in the Java controller.
        Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);

        using var reloaded = await _fixture.Client.GetAsync("/settings/retrive?channelName=channel-e2e");
        var updated = Assert.Single((await reloaded.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("updated", updated.GetProperty("description").GetString());

        using var deleted = await _fixture.Client.DeleteAsync($"/settings/delete?id={id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var afterDelete = await _fixture.Client.GetAsync("/settings/retrive?channelName=channel-e2e");
        var remaining = await afterDelete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(remaining.EnumerateArray());
    }

    [Fact]
    public async Task StartLive_ValidatesTheRequestAndRecordsTheFailureOfTheStreamer()
    {
        using var invalid = await _fixture.Client.PostAsJsonAsync("/live/start-live", new { streamUrl = "rtmp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var emptyFolder = Path.Combine(_fixture.DataDirectory, "empty-folder");
        Directory.CreateDirectory(emptyFolder);
        using var noVideos = await _fixture.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = "rtmp://ingest.example/live",
            streamKey = "stream-key",
            videoPath = emptyFolder,
            platformStreamName = "channel-e2e-empty",
            channelName = "channel-e2e-empty"
        });
        Assert.Equal(HttpStatusCode.NotFound, noVideos.StatusCode);

        using var missing = await _fixture.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = "rtmp://ingest.example/live",
            streamKey = "stream-key",
            videoPath = $"\"{Path.Combine(_fixture.DataDirectory, "missing clip.mp4")}\"",
            platformStreamName = "channel-e2e-missing",
            channelName = "channel-e2e-missing"
        });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var folder = Path.Combine(_fixture.DataDirectory, "clips");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "clip.mp4"), [0x00, 0x01, 0x02]);

        using var started = await _fixture.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = "rtmp://ingest.example/live",
            streamKey = "stream-key",
            videoPath = folder,
            platformStreamName = "channel-e2e-live",
            channelName = "channel-e2e-live",
            videoSettingsRecord = new
            {
                id = TwitchVideoSettingId,
                title = "Default Twitch",
                isDefaultConfiguration = true,
                defaultPlatformConfiguration = "Twitch",
                videoCodec = 27,
                videoCodecName = "libx264",
                pixelFormat = 0,
                videoBitrate = 5_000_000,
                videoFormat = "flv",
                gopSize = 2,
                isVideoAndAudioSettingActive = true,
                audioSettingRecord = new { audioCodec = 86018, audioBitrate = 128_000 }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        // ffmpeg is not installed in the test environment: the row must exist and the failure must
        // be reported through the video status/message instead of an HTTP error.
        VideoSnapshot? video = null;
        for (var attempt = 0; attempt < 40 && video is null; attempt++)
        {
            using var list = await _fixture.Client.GetAsync("/video/getPage?size=50&sort=pkid,desc");
            var page = await list.Content.ReadFromJsonAsync<JsonElement>();
            video = page.GetProperty("content").EnumerateArray()
                .Select(item => new VideoSnapshot(
                    item.GetProperty("pkid").GetInt32(),
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("liveStatus").GetString() ?? string.Empty,
                    item.GetProperty("message").GetString()))
                .FirstOrDefault(item => item.Name == "clip.mp4");

            if (video is null)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotNull(video);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (video!.Status != "ERROR" && video.Status != "ENDED" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            using var list = await _fixture.Client.GetAsync("/video/getPage?size=50&sort=pkid,desc");
            var page = await list.Content.ReadFromJsonAsync<JsonElement>();
            var current = page.GetProperty("content").EnumerateArray()
                .Select(item => new VideoSnapshot(
                    item.GetProperty("pkid").GetInt32(),
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("liveStatus").GetString() ?? string.Empty,
                    item.GetProperty("message").GetString()))
                .First(item => item.Pkid == video.Pkid);
            video = current;
        }

        Assert.Equal("ERROR", video!.Status);
        Assert.Contains("ffprobe-not-installed", video.Message!, StringComparison.Ordinal);
        Assert.Equal("mp4", await ExtensionOf(video.Pkid));
        await AssertRelationsOf(video.Pkid);

        using var stopped = await _fixture.Client.PutAsync($"/live/stop-live?videoLivePkid={video.Pkid}", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);

        // unlockVideo answered HttpStatus.ACCEPTED: the unlock runs in the task executor.
        using var lockResponse = await _fixture.Client.PutAsync($"/video/unlockVideo?videoKey={video.Pkid}", null);
        Assert.Equal(HttpStatusCode.Accepted, lockResponse.StatusCode);
    }

    [Fact]
    public async Task MainLive_RowFormsCarryTheVideoKeyInTheirAction()
    {
        // asp-all-route-data replaces the whole route value dictionary, so the asp-route-pkid of the
        // sibling attribute was silently dropped and Stop/Replay/Unlock ran with pkid = 0, which the
        // services reported as "Video non trovato".
        var folder = Path.Combine(_fixture.DataDirectory, "row-forms");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "row.mp4"), [0x00, 0x01, 0x02]);

        using var started = await _fixture.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = "rtmp://ingest.example/live",
            streamKey = "stream-key",
            videoPath = folder,
            platformStreamName = "channel-e2e-row",
            channelName = "channel-e2e-row",
            videoSettingsRecord = new
            {
                id = TwitchVideoSettingId,
                title = "Default Twitch",
                isDefaultConfiguration = true,
                defaultPlatformConfiguration = "Twitch",
                videoCodec = 27,
                videoCodecName = "libx264",
                pixelFormat = 0,
                videoBitrate = 5_000_000,
                videoFormat = "flv",
                gopSize = 2,
                isVideoAndAudioSettingActive = true,
                audioSettingRecord = new { audioCodec = 86018, audioBitrate = 128_000 }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var pkid = 0;
        for (var attempt = 0; attempt < 40 && pkid == 0; attempt++)
        {
            using var list = await _fixture.Client.GetAsync("/video/getPage?size=200&sort=pkid,desc");
            var page = await list.Content.ReadFromJsonAsync<JsonElement>();
            pkid = page.GetProperty("content").EnumerateArray()
                .Where(item => item.GetProperty("name").GetString() == "row.mp4")
                .Select(item => item.GetProperty("pkid").GetInt32())
                .FirstOrDefault();
            if (pkid == 0)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotEqual(0, pkid);

        using var html = await _fixture.Client.GetAsync("/orbis/mainLive?channelName=channel-e2e-row");
        var body = await html.Content.ReadAsStringAsync();

        var actions = Regex.Matches(body, "<form[^>]*>")
            .Select(match => match.Value)
            .Where(form => form.Contains("action=\"", StringComparison.Ordinal))
            .Where(form => form.Contains("handler=Stop", StringComparison.Ordinal)
                || form.Contains("handler=Replay", StringComparison.Ordinal)
                || form.Contains("handler=Unlock", StringComparison.Ordinal))
            .Select(form => form[form.IndexOf("action=\"", StringComparison.Ordinal)..])
            // The page carries the ampersands of the query string escaped: they are the separators
            // this test looks for, so the address is read as the browser will send it.
            .Select(form => WebUtility.HtmlDecode(form))
            .ToList();

        var rowForms = actions.Where(action => !action.Contains("handler=Unlock", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rowForms);
        Assert.All(rowForms, action => Assert.Matches(@"[?&]pkid=\d+", action));
        Assert.Contains(rowForms, action => action.Contains($"pkid={pkid}", StringComparison.Ordinal));

        var unlockForms = actions.Where(action => action.Contains("handler=Unlock", StringComparison.Ordinal)).ToList();
        Assert.All(unlockForms, action => Assert.Matches(@"[?&]videoKey=\d+", action));

        // The row says it is working: a spinner sits with the icons that start, stop and restart, and
        // it only shows once the form is really on its way, not while the question is being asked.
        Assert.Contains("data-row", body, StringComparison.Ordinal);
        Assert.Contains("data-while=\"busy\"", body, StringComparison.Ordinal);
        Assert.Contains("class=\"spinner\"", body, StringComparison.Ordinal);

        // The rows are redone when the server says one moved, from the change they were drawn from,
        // and never on a timer.
        Assert.Matches("data-refresh=\"[^\"]*handler=Rows[^\"]*\" data-since=\"\\d+\"", body);
        Assert.DoesNotContain("data-interval", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainLive_StartLiveOpensWithUrlParameter()
    {
        // Without the shortcut the wizard is closed, and it is not the script that opens it: a page
        // that arrives with the link already shows it.
        using var plain = await _fixture.Client.GetAsync("/orbis/mainLive");
        var plainBody = await plain.Content.ReadAsStringAsync();
        Assert.Contains("id=\"start-dialog\" data-busy-host>", plainBody, StringComparison.Ordinal);

        using var asked = await _fixture.Client.GetAsync("/orbis/mainLive?start=1");
        var askedBody = await asked.Content.ReadAsStringAsync();
        Assert.Contains("id=\"start-dialog\" data-busy-host open", askedBody, StringComparison.Ordinal);

        // Starting a live walks the folder and starts ffmpeg on the server: the dialog says so
        // next to its button instead of looking inert.
        Assert.Contains("data-while=\"busy\"", plainBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskManager_ShowsTheMetersStraightAwayAndTheFactsOnTheirOwn()
    {
        using var page = await _fixture.Client.GetAsync("/orbis/mainTaskManager");
        var body = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The meters of the Java build: the test host answers in English. They are the cheap
        // part of the page, so they are in the first answer. The processor has a card of its own
        // and the card another: which of the two the machine answers decides which of them stays.
        Assert.Contains("ring", body, StringComparison.Ordinal);
        Assert.Contains("CPU TEMPERATURE", body, StringComparison.Ordinal);
        Assert.Contains("GPU TEMPERATURE", body, StringComparison.Ordinal);
        Assert.Contains("SWAP", body, StringComparison.Ordinal);

        // What the machine is needs WMI, which is why the panel is asked for on its own: the page
        // says where it will come from, shows a loading only if the answer is late, and does not
        // wait for it to open.
        Assert.Contains("data-load=\"/orbis/mainTaskManager?handler=Facts\"", body, StringComparison.Ordinal);
        Assert.Contains("data-while=\"loading\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, body, StringComparison.Ordinal);

        using var facts = await _fixture.Client.GetAsync("/orbis/mainTaskManager?handler=Facts");
        var panel = await facts.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);

        // The panel itself: the labels come from the bundle and the values from the machine, so at
        // least the machine name and the runtime are always there.
        Assert.Contains("class=\"facts\"", panel, StringComparison.Ordinal);
        Assert.Contains(Environment.MachineName, panel, StringComparison.Ordinal);
        Assert.Contains(".NET", panel, StringComparison.Ordinal);

        // And it is the only thing in it: no meter travels with the panel.
        Assert.DoesNotContain("ring", panel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskManager_PushesTheMetersInsteadOfBeingAskedForThem()
    {
        // The page is painted by the push channel: it carries the region the values arrive into and
        // the name of each meter, so the script can fill them without asking the server again.
        using var page = await _fixture.Client.GetAsync("/orbis/mainTaskManager");
        var body = await page.Content.ReadAsStringAsync();

        Assert.Contains("data-stats", body, StringComparison.Ordinal);
        Assert.DoesNotContain("data-interval", body, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Stats", body, StringComparison.Ordinal);

        foreach (var key in new[] { "cpu", "ram", "swap", "cpu_temperature", "gpu_temperature" })
        {
            Assert.Contains($"data-key=\"{key}\"", body, StringComparison.Ordinal);
        }

        // Nothing is measured before the page goes out: the meters start in their loading and the
        // first sample of the channel ends it.
        Assert.Contains("data-stats data-state=\"loading\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updates_SaysWhenARowMovedAndOnlyWhenSomeoneIsListening()
    {
        // The channel of the changes: it answers the counters at once, and a rows message only
        // after something really moved. The answer never ends, so it is read as it comes.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/updates?watch=stats");
        using var page = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await page.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/event-stream", page.Content.Headers.ContentType?.MediaType);

        Assert.Equal("retry: 2000", await reader.ReadLineAsync());
        Assert.Equal(string.Empty, await reader.ReadLineAsync());

        // A counter is not a change, so it carries no id: the browser keeps the last change number
        // and resumes the stream from there.
        Assert.Equal("event: stats", await reader.ReadLineAsync());

        var line = await reader.ReadLineAsync() ?? string.Empty;
        Assert.StartsWith("data: {", line, StringComparison.Ordinal);

        // Under the names the cards carry, not the API ones ("CPU", "TEMPERATURA CPU"): a name that
        // does not match leaves the meter on its dash forever, with nothing to say it is wrong.
        using var values = System.Text.Json.JsonDocument.Parse(line["data: ".Length..]);
        foreach (var key in new[] { "cpu", "ram", "swap", "cpu_temperature", "gpu_temperature" })
        {
            Assert.True(values.RootElement.TryGetProperty(key, out _), $"missing {key} in {line}");
        }

        Assert.Equal(string.Empty, await reader.ReadLineAsync());

        // A window that asked for nothing is not left with a connection open doing nothing.
        using var silent = await _fixture.Client.GetAsync("/updates");
        Assert.Equal("", await silent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Updates_SendsRowsOnlyWhenARowReallyMoved()
    {
        var notifier = _fixture.Services.GetRequiredService<LiveChangeNotifier>();

        // From the change the page was drawn from: nothing to say until something moves.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/updates?watch=rows&since={notifier.Version}");
        using var page = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await page.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The hello says how long to wait after a dropped stream, and it is what makes the browser
        // see the stream as open before there is anything to say.
        Assert.Equal("retry: 2000", await reader.ReadLineAsync(cancel.Token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(cancel.Token));

        var reading = reader.ReadLineAsync(cancel.Token);

        // The rows are asked again only when a live row changed, and the number that comes with the
        // message is the change the page must resume from after a reconnect.
        notifier.Raise();
        Assert.Equal($"id: {notifier.Version}", await reading);

        Assert.Equal("event: rows", await reader.ReadLineAsync(cancel.Token));
        Assert.Equal($"data: {notifier.Version}", await reader.ReadLineAsync(cancel.Token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(cancel.Token));

        // A page that asks from a change older than the last one is told at once, instead of waiting
        // for a change that may not come: that is the reconnect of a page whose stream was closed.
        using var ask = new HttpRequestMessage(HttpMethod.Get, $"/updates?watch=rows&since={Math.Max(0, notifier.Version - 1)}");
        using var resumed = await _fixture.Client.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead);
        using var resumedReader = new StreamReader(await resumed.Content.ReadAsStreamAsync());

        Assert.Equal("retry: 2000", await resumedReader.ReadLineAsync(cancel.Token));
        Assert.Equal(string.Empty, await resumedReader.ReadLineAsync(cancel.Token));
        Assert.Equal($"id: {notifier.Version}", await resumedReader.ReadLineAsync(cancel.Token));
        Assert.Equal("event: rows", await resumedReader.ReadLineAsync(cancel.Token));
    }

    /// <summary>The live page needs the platform (history) and the setting of every video.</summary>
    private async Task AssertRelationsOf(int videoKey)
    {
        using var list = await _fixture.Client.GetAsync("/video/getAllVideo?size=200&sort=pkid,desc");
        var item = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").EnumerateArray()
            .First(candidate => candidate.GetProperty("pkid").GetInt32() == videoKey);

        Assert.Equal("channel-e2e-live", item.GetProperty("videoLiveHistory").GetProperty("platformStreamName").GetString());
        Assert.Equal("libx264", item.GetProperty("videoSetting").GetProperty("videoCodecName").GetString());
    }

    private async Task<string?> ExtensionOf(int videoKey)
    {
        using var list = await _fixture.Client.GetAsync($"/video/getAllVideo?size=200&sort=pkid,desc");
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("content").EnumerateArray()
            .First(item => item.GetProperty("pkid").GetInt32() == videoKey)
            .GetProperty("extension")
            .GetString();
    }

    [Fact]
    public async Task VideoSettings_ExposeTheDefaultPlatformConfigurations()
    {
        using var response = await _fixture.Client.GetAsync("/video-setting/getVideoSettings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await response.Content.ReadFromJsonAsync<JsonElement>();
        var platforms = settings.EnumerateArray()
            .Select(item => item.GetProperty("defaultPlatformConfiguration").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Twitch", platforms);
        Assert.Contains("Youtube", platforms);
    }

    [Fact]
    public async Task SystemInformation_IsServedAsPlainNumbers()
    {
        foreach (var endpoint in new[] { "/cpu", "/ram", "/swap" })
        {
            using var response = await _fixture.Client.GetAsync($"/taskManager/statistics{endpoint}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var value = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.InRange(value.GetDouble(), 0d, 100d);
        }

        using var all = await _fixture.Client.GetAsync("/taskManager/statistics/allInfo");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var fields = (await all.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(item => item.GetProperty("field").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CPU", fields);
        Assert.Contains("RAM", fields);
        Assert.Contains("TEMPERATURA CPU", fields);
        Assert.Contains("TEMPERATURA GPU", fields);
    }

    [Theory]
    [InlineData("/cpu/temperature")]
    [InlineData("/gpu/temperature")]
    public async Task Temperatures_AreServedAsPlainNumbersOrAsNothingAtAll(string endpoint)
    {
        // A machine with no sensor for a temperature answers -1 rather than a made up number: the
        // meter reads that as the reason to leave the page instead of showing a dash forever.
        using var response = await _fixture.Client.GetAsync($"/taskManager/statistics{endpoint}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        var celsius = value.GetDouble();
        Assert.True(celsius == SystemInfoProviderFactory.NotAvailable || celsius is >= 1 and <= 120, $"{endpoint} answered {celsius}");
    }

    [Fact]
    public async Task LanguageQueryParameterIsNotAFilter()
    {
        using var response = await _fixture.Client.GetAsync("/settings/retrive?lang=it");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record VideoSnapshot(int Pkid, string Name, string Status, string? Message);
}
