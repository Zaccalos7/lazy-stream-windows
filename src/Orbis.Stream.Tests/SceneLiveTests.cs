using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>
/// A live started from the canvas, the way the live wizard starts every live now: one row on the
/// live page standing on its base source, the light preview of the composed picture, and a stop
/// that reaches the ffmpeg of the canvas. Files are the only sources a test machine has, and they
/// go through the same composition graph as a screen or a camera. Skipped without ffmpeg.
/// </summary>
public sealed class SceneLiveTests : IAsyncLifetime
{
    private TestHostRunner _host = null!;
    private readonly ITestOutputHelper _output;

    public SceneLiveTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task SceneLive_IsOneRow_ShowsItsLightPreview_AndStopsFromItsRow()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the scene live test is skipped.");
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output"));
        var background = Path.Combine(folder, "background.mp4");
        var overlay = Path.Combine(folder, "overlay.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 30 -pix_fmt yuv420p \"{background}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=size=160x90:rate=15 -t 30 -pix_fmt yuv420p \"{overlay}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Test scene", null, 640, 360,
            [
                new SceneItemRequest(SourceKind.File, background, "Background", 0, 0, 640, 360, false),
                new SceneItemRequest(SourceKind.File, overlay, "Overlay", 400, 20, 220, 124, false)
            ]));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri,
            "scene.flv",
            "scene-platform",
            "scene-channel",
            Setting()));

        // One row for the whole canvas, standing on its base source: the row its ffmpeg is under.
        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "scene-channel", new PageRequest(0, 10, [])).Content);
        Assert.Equal("Test scene", Row().SceneName);
        Assert.Equal(2, Row().Total);
        Assert.Equal("Background", Row().Video.Name);

        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas never went live: " + Row().Video.Message);

        // The dialog that opens from the row shows the composition, so it is called a scene there and
        // not a playlist: the row is not one folder of videos that played one after the other.
        var words = _host.Services.GetRequiredService<Orbis.Stream.Core.I18n.UiText>();
        var page = await _host.Client.GetStringAsync("/orbis/mainLive?channelName=scene-channel");
        Assert.Contains($"title=\"{words["sceneDetails"]}\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"title=\"{words["playlistDetails"]}\"", page, StringComparison.Ordinal);

        // The composed picture is split in the graph: the preview frame is written next to the live.
        var frames = _host.Services.GetRequiredService<LivePreviewFrames>();
        var basePkid = Row().Video.Pkid!.Value;
        Assert.True(await WaitForAsync(() => frames.Latest(basePkid) is { Bytes.Length: > 0 }), "no preview frame was written");
        var frame = frames.Latest(basePkid)!.Bytes;
        Assert.Equal(0xFF, frame[0]);
        Assert.Equal(0xD8, frame[1]);

        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the stop of the row did not reach the canvas");
        Assert.True(await WaitForAsync(() => frames.Latest(basePkid) is null), "a stopped live left its preview frame behind");

        // The scene a live went on air with is what it restarts from: it cannot be deleted on its own.
        Assert.Throws<LiveException>(() => _host.Services.GetRequiredService<SceneService>().Delete(scenePkid));

        // The rows still carry everything the live needs (sources, places, setting): with the scene
        // gone all the same (a database from before layouts), the play of the row puts the same
        // canvas back on air.
        _host.Services.GetRequiredService<SceneRepository>().Delete(scenePkid);
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(basePkid));
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the row did not restart without its scene: " + Row().Video.Message);
        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the restarted canvas did not stop");
    }

    [Fact]
    public async Task SceneLive_StoppedFromAnyOfItsRows_StopsTheWholeComposition()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the scene live test is skipped.");
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output"));
        var background = Path.Combine(folder, "background.mp4");
        var overlay = Path.Combine(folder, "overlay.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 30 -pix_fmt yuv420p \"{background}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=size=160x90:rate=15 -t 30 -pix_fmt yuv420p \"{overlay}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Test scene", null, 640, 360,
            [
                new SceneItemRequest(SourceKind.File, background, "Background", 0, 0, 640, 360, false),
                new SceneItemRequest(SourceKind.File, overlay, "Overlay", 400, 20, 220, 124, false)
            ]));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri,
            "scene.flv",
            "scene-platform",
            "scene-channel",
            Setting()));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "scene-channel", new PageRequest(0, 10, [])).Content);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas never went live: " + Row().Video.Message);

        // The stop can come from any row of the composition, not only from the one its ffmpeg runs on.
        var rows = repository.FindByLiveStatusAndChannelName(LiveStatus.Live, "scene-channel");
        Assert.Equal(2, rows.Count);
        var otherRow = Assert.Single(rows, row => row.Pkid != rows[0].Pkid);

        streaming.StopVideoStreamingByPkid(otherRow.Pkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "stopping a row of the canvas left it on air");

        // And it leaves no flag behind for the next play of the same composition.
        var historyPkid = otherRow.VideoLiveHistoryId!.Value;
        Assert.All(repository.FindByLiveHistoryId(historyPkid), row =>
            Assert.False(row.ShouldBeStop, $"row {row.Pkid} ({row.Name}) kept its stop flag"));
    }

    [Fact]
    public async Task SceneLive_TurnsLowLatencyOn_ForTheSettingOfTheRowsAndNotForTheOneTheUserChose()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the scene live test is skipped.");
            return;
        }

        var scenePkid = await TwoFileSceneAsync();
        var settings = _host.Services.GetRequiredService<VideoSettingRepository>();
        var videos = _host.Services.GetRequiredService<VideoRepository>();

        // The configuration the user keeps, with low latency off on purpose: it is a re-stream and
        // the lookahead is what stops the encoder starving itself after every keyframe.
        var chosen = Setting() with { VideoOptions = [new VideoOptionRequest(VideoSettingLatency.OptionKey, "0")] };
        var chosenId = settings.Insert(chosen.ToEntity());
        Assert.False(VideoSettingLatency.IsOn(settings.FindById(chosenId)!));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri,
            "latency.flv",
            "latency-platform",
            "latency-channel",
            chosen));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "latency-channel", new PageRequest(0, 10, [])).Content);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas never went live: " + Row().Video.Message);

        var basePkid = Row().Video.Pkid!.Value;
        var rows = videos.FindByLiveHistoryId(videos.FindByPkid(basePkid)!.VideoLiveHistoryId!.Value);
        Assert.Equal(2, rows.Count);

        // Two sources are one picture, so the canvas decides the switch on for itself. Both rows
        // go on air on the same setting: a composition cannot stream its halves differently.
        var onAirIds = rows.Select(row => row.VideoSettingId!.Value).Distinct().ToArray();
        Assert.Single(onAirIds);
        Assert.NotEqual(chosenId, onAirIds[0]);
        var onAir = settings.FindById(onAirIds[0])!;
        Assert.True(VideoSettingLatency.IsOn(onAir));

        // The switch is the only thing that changed on it: everything the user configured is what
        // this live encodes with.
        Assert.Equal(Setting().Title, onAir.Title);
        Assert.Equal(Setting().VideoCodec, onAir.VideoCodec);
        Assert.Equal(Setting().VideoBitrate, onAir.VideoBitrate);
        Assert.Equal(Setting().GopSize, onAir.GopSize);
        Assert.Equal(Setting().AudioSettingRecord!.AudioBitrate, onAir.AudioSetting!.AudioBitrate);
        Assert.Single(onAir.VideoSettingsOptions, o => o.Key == VideoSettingLatency.OptionKey);

        // And the configuration the user chose is left exactly as it was.
        var untouched = settings.FindById(chosenId)!;
        Assert.False(VideoSettingLatency.IsOn(untouched));
        Assert.Equal(
            "0",
            untouched.VideoSettingsOptions.Single(o => o.Key == VideoSettingLatency.OptionKey).Value);

        // A second play of the same composition reuses that setting instead of making another one.
        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the canvas did not stop");
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(basePkid));
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas did not restart: " + Row().Video.Message);

        Assert.Equal(onAirIds[0], videos.FindByPkid(basePkid)!.VideoSettingId!.Value);
        streaming.StopVideoStreamingByPkid(basePkid);
    }

    [Fact]
    public async Task SceneLive_OfOneSource_LeavesTheConfigurationOfTheUserAlone()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the scene live test is skipped.");
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "sources");
        Directory.CreateDirectory(folder);
        var lonely = Path.Combine(folder, "lonely.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 30 -pix_fmt yuv420p \"{lonely}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Lonely scene", null, 640, 360,
            [new SceneItemRequest(SourceKind.File, lonely, "Only", 0, 0, 640, 360, false)]));

        var settings = _host.Services.GetRequiredService<VideoSettingRepository>();
        var videos = _host.Services.GetRequiredService<VideoRepository>();
        var chosen = Setting() with { VideoOptions = [new VideoOptionRequest(VideoSettingLatency.OptionKey, "0")] };
        var chosenId = settings.Insert(chosen.ToEntity());

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri,
            "lonely.flv",
            "lonely-platform",
            "lonely-channel",
            chosen));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "lonely-channel", new PageRequest(0, 10, [])).Content);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas never went live: " + Row().Video.Message);

        // One source is not a composition, so nothing decides anything for it: the live goes on air
        // with the configuration the user chose, low latency off as they left it.
        var basePkid = Row().Video.Pkid!.Value;
        var streamingWith = settings.FindById(videos.FindByPkid(basePkid)!.VideoSettingId!.Value)!;
        Assert.False(VideoSettingLatency.IsOn(streamingWith));
        Assert.False(VideoSettingLatency.IsOn(settings.FindById(chosenId)!));

        streaming.StopVideoStreamingByPkid(basePkid);
    }

    [Fact]
    public async Task SceneLive_EnqueueSpot_StreamsSpotThroughAndResumesScene()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "scene-spot-sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output-scene-spot"));
        var background = Path.Combine(folder, "scene-bg.mp4");
        var spotVideo = Path.Combine(folder, "scene-spot.mp4");

        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 15 -pix_fmt yuv420p \"{background}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=size=160x90:rate=15 -t 3 -pix_fmt yuv420p \"{spotVideo}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Spot Scene", null, 640, 360,
            [
                new SceneItemRequest(SourceKind.File, background, "Background", 0, 0, 640, 360, false)
            ]));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output-scene-spot")).AbsoluteUri,
            "scene-spot.flv",
            "scene-spot-platform",
            "scene-spot-channel",
            Setting()));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "scene-spot-channel", new PageRequest(0, 10, [])).Content);

        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "scene never went live");

        var basePkid = Row().Video.Pkid!.Value;

        // Enqueue spot
        streaming.EnqueueSpot(spotVideo, basePkid);

        // While spot is streaming, status must stay Live and paused position must be saved in repository
        await Task.Delay(1500);
        Assert.Equal(LiveStatus.Live, Row().Status);
        Assert.True(repository.FindByPkid(basePkid)!.LastTimeStampBeforeStop > 0, "LastTimeStampBeforeStop was not saved in repository when spot yielded");

        // After spot finishes, scene should resume and eventually reach end
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Ended), "scene never finished after spot: " + Row().Status + " " + Row().Video.Message);
    }

    [Fact]
    public async Task SceneLive_StopAndReplay_ResumesFromLastTimeStamp()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "scene-stop-sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output-scene-stop"));
        var background = Path.Combine(folder, "scene-stop-bg.mp4");

        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 15 -pix_fmt yuv420p \"{background}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Stop Scene", null, 640, 360,
            [
                new SceneItemRequest(SourceKind.File, background, "Background", 0, 0, 640, 360, false)
            ]));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid,
            new Uri(Path.Combine(_host.DataDirectory, "output-scene-stop")).AbsoluteUri,
            "scene-stop.flv",
            "scene-stop-platform",
            "scene-stop-channel",
            Setting()));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "scene-stop-channel", new PageRequest(0, 10, [])).Content);

        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "scene never went live");

        var basePkid = Row().Video.Pkid!.Value;

        // Stream for a few seconds
        await Task.Delay(2500);

        // Stop the live
        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Stopped), "scene did not stop");

        var stoppedPosition = repository.FindByPkid(basePkid)!.LastTimeStampBeforeStop;
        Assert.True(stoppedPosition > 0, "LastTimeStampBeforeStop was not saved on stop");

        // Replay the scene
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(basePkid));
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "scene did not restart");

        // It must not reset LastTimeStampBeforeStop to 0
        Assert.True(repository.FindByPkid(basePkid)!.LastTimeStampBeforeStop >= stoppedPosition,
            "LastTimeStampBeforeStop was reset to zero upon replay");

        // Eventually finishes
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Ended), "scene never finished");
    }

    /// <summary>Two files laid over each other, which is what a composition is made of.</summary>
    private async Task<long> TwoFileSceneAsync()
    {
        var folder = Path.Combine(_host.DataDirectory, "sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output"));
        var background = Path.Combine(folder, "latency-background.mp4");
        var overlay = Path.Combine(folder, "latency-overlay.mp4");
        var ffmpeg = Tool("ffmpeg")!;
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x180:rate=15 -t 30 -pix_fmt yuv420p \"{background}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=size=160x90:rate=15 -t 30 -pix_fmt yuv420p \"{overlay}\"");

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Latency scene", null, 640, 360,
            [
                new SceneItemRequest(SourceKind.File, background, "Background", 0, 0, 640, 360, false),
                new SceneItemRequest(SourceKind.File, overlay, "Overlay", 400, 20, 220, 124, false)
            ]));

        return scenePkid;
    }

    private static VideoSettingsRequest Setting() => JsonSerializer.Deserialize<VideoSettingsRequest>(
        """
        {
          "title": "Test", "videoCodec": 27, "videoCodecName": "libx264", "pixelFormat": 0,
          "videoBitrate": 500000, "videoFormat": "flv", "gopSize": 2, "isVideoAndAudioSettingActive": true,
          "audioSettingRecord": { "audioCodec": 86018, "audioBitrate": 64000 }
        }
        """,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task RunAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException($"Unable to start {fileName}");

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} {arguments} failed: {error}");
    }

    private static string? Tool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
}
