using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
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

        // The composed picture is split in the graph: the preview frame is written next to the live.
        var frames = _host.Services.GetRequiredService<LivePreviewFrames>();
        var basePkid = Row().Video.Pkid!.Value;
        Assert.True(await WaitForAsync(() => frames.Read(basePkid) is { Length: > 0 }), "no preview frame was written");
        var frame = frames.Read(basePkid)!;
        Assert.Equal(0xFF, frame[0]);
        Assert.Equal(0xD8, frame[1]);

        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the stop of the row did not reach the canvas");
        Assert.True(await WaitForAsync(() => frames.Read(basePkid) is null), "a stopped live left its preview frame behind");

        // The rows carry everything the live needs (sources, places, setting): with the scene gone
        // from the composer, the play of the row still puts the same canvas back on air.
        _host.Services.GetRequiredService<SceneService>().Delete(scenePkid);
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(basePkid));
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the row did not restart without its scene: " + Row().Video.Message);
        streaming.StopVideoStreamingByPkid(basePkid);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the restarted canvas did not stop");
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
