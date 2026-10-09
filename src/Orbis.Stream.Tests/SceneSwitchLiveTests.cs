using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// The scene deck on a live that is really streaming: a picture stands in for the live until it is
/// resumed, a clip goes back to the live by itself, and the live carries on from where it was left.
/// What is on air is read off the pixels of the preview, which is the picture the live is encoding.
/// The whole class needs ffmpeg and is skipped without it.
/// </summary>
public sealed class SceneSwitchLiveTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The colours on air: the program, the picture of a button, the clip of another.</summary>
    private static readonly (int R, int G, int B) Program = (32, 80, 192);
    private static readonly (int R, int G, int B) Magenta = (255, 0, 255);
    private static readonly (int R, int G, int B) Yellow = (255, 255, 0);

    private TestHostRunner _host = null!;
    private readonly ITestOutputHelper _output;

    public SceneSwitchLiveTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private bool HasFfmpeg()
    {
        if (Tool("ffmpeg") is not null && Tool("ffprobe") is not null)
        {
            return true;
        }

        _output.WriteLine("ffmpeg/ffprobe are not installed: the scene deck live test is skipped.");
        return false;
    }

    [Fact]
    public async Task A_picture_stands_in_until_it_is_resumed_and_a_clip_goes_back_by_itself()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        var ffmpeg = Tool("ffmpeg")!;
        var folder = Path.Combine(_host.DataDirectory, "deck-sources");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "deck-output"));

        // The program: a blue video with a sound, long enough to be switched away from and back.
        var program = Path.Combine(folder, "program.mp4");
        await RunAsync(ffmpeg,
            "-y -f lavfi -i color=c=0x2050c0:s=640x360:r=15 -f lavfi -i sine=frequency=440:sample_rate=44100 "
            + $"-t 60 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \"{program}\"");
        var clip = Path.Combine(folder, "intro.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i color=c=yellow:s=640x360:r=15 -t 3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{clip}\"");

        var brb = await ButtonAsync("BRB", "brb.png", TestPictures.Png(640, 360, (_, _) => (255, 0, 255, 255)));
        var intro = await ButtonAsync("Intro", "intro.mp4", await File.ReadAllBytesAsync(clip));

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartLive(new StartLiveRequest(
            new Uri(Path.Combine(_host.DataDirectory, "deck-output")).AbsoluteUri,
            "deck.flv",
            program,
            "deck-platform",
            "deck-channel",
            Setting()));

        var row = repository.FindLivePage(null, "deck-channel", 0, 10).Items.Single().Video;
        Assert.True(await WaitForAsync(() => repository.FindByPkid(row.Pkid)?.LiveStatus == LiveStatus.Live), "the program never went live");
        Assert.True(await OnAirAsync(row.Pkid, Program), "the program is not what the preview shows");

        // The picture of a button goes on air in place of the program, and stays.
        await Task.Delay(TimeSpan.FromSeconds(2));
        using (var play = await _host.Client.PostAsync($"/live/{row.Pkid}/scene/{brb.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, play.StatusCode);
        }

        Assert.True(await OnAirAsync(row.Pkid, Magenta), "the picture never took the place of the program");
        var state = await StateAsync(row.Pkid);
        Assert.Equal(("BRB", "IMAGE", true), (state.Current!.Label, state.Current.Kind, state.Current.Holds));
        Assert.Contains("BRB", repository.FindByPkid(row.Pkid)!.Message, StringComparison.Ordinal);
        Assert.Equal(LiveStatus.Live, repository.FindByPkid(row.Pkid)!.LiveStatus);
        var paused = repository.FindByPkid(row.Pkid)!.LastTimeStampBeforeStop;
        Assert.True(paused > 0, "the program was not left at a position");

        // The row of the live says how to go back, pulsing; pressing the button again changes nothing.
        Assert.Contains("data-scene-row-resume", await RowsAsync(), StringComparison.Ordinal);
        using (var again = await _host.Client.PostAsync($"/live/{row.Pkid}/scene/{brb.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(await OnAirAsync(row.Pkid, Magenta), "the picture did not stay on air");

        // "Resume live": the program comes back, from where it was left.
        using (var resume = await _host.Client.PostAsync($"/live/{row.Pkid}/scene/resume", null))
        {
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        }

        Assert.True(await OnAirAsync(row.Pkid, Program), "the program did not come back after Resume live");
        Assert.Null((await StateAsync(row.Pkid)).Current);
        Assert.DoesNotContain("data-scene-row-resume", await RowsAsync(), StringComparison.Ordinal);
        Assert.True(await WaitForAsync(() => _host.Services.GetRequiredService<StreamingSessionRegistry>().TryGet(row.Pkid, out var session)
            && session!.PositionMilliseconds >= paused), "the program did not carry on from where it was left");

        // A clip plays once and goes back to the program by itself, with no Resume live to show.
        using (var play = await _host.Client.PostAsync($"/live/{row.Pkid}/scene/{intro.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, play.StatusCode);
        }

        Assert.True(await OnAirAsync(row.Pkid, Yellow), "the clip never took the place of the program");
        var clipState = await StateAsync(row.Pkid);
        Assert.Equal(("Intro", false), (clipState.Current!.Label, clipState.Current.Holds));
        Assert.DoesNotContain("data-scene-row-resume", await RowsAsync(), StringComparison.Ordinal);
        Assert.True(await WaitForAsync(async () => (await StateAsync(row.Pkid)).Current is null), "the clip never ended");
        Assert.True(await OnAirAsync(row.Pkid, Program), "the program did not come back after the clip");

        // A stop while a picture stands in stops the live, and leaves the program where it was.
        using (var play = await _host.Client.PostAsync($"/live/{row.Pkid}/scene/{brb.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, play.StatusCode);
        }

        Assert.True(await OnAirAsync(row.Pkid, Magenta), "the picture never went on air the second time");
        var left = repository.FindByPkid(row.Pkid)!.LastTimeStampBeforeStop;
        streaming.StopVideoStreamingByPkid(row.Pkid);
        Assert.True(await WaitForAsync(() => repository.FindByPkid(row.Pkid)?.LiveStatus == LiveStatus.Stopped), "the live did not stop");
        Assert.Equal(left, repository.FindByPkid(row.Pkid)!.LastTimeStampBeforeStop);
        Assert.True(await WaitForAsync(async () => !(await StateAsync(row.Pkid)).Live), "the deck still takes buttons for a live that stopped");
    }

    [Fact]
    public async Task A_canvas_hands_over_to_a_picture_and_comes_back()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        var ffmpeg = Tool("ffmpeg")!;
        var folder = Path.Combine(_host.DataDirectory, "canvas-sources");
        var output = Path.Combine(_host.DataDirectory, "canvas-output");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(output);
        var video = Path.Combine(folder, "blue.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i color=c=0x2050c0:s=640x360:r=15 -t 60 -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{video}\"");
        var brb = await ButtonAsync("Pausa", "pausa.png", TestPictures.Png(320, 180, (_, _) => (255, 0, 255, 255)));

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(
            null, "Deck canvas", null, 640, 360, [new SceneItemRequest(SourceKind.File, video, "Video", 0, 0, 640, 360, false)]));
        var streaming = _host.Services.GetRequiredService<StreamingService>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid, new Uri(output).AbsoluteUri, "canvas.flv", "canvas-platform", "canvas-channel", Setting()));

        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "canvas-channel", new PageRequest(0, 10, [])).Content);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the canvas never went live: " + Row().Video.Message);
        var pkid = Row().Video.Pkid!.Value;
        Assert.True(await OnAirAsync(pkid, Program), "the canvas is not what the preview shows");

        using (var play = await _host.Client.PostAsync($"/live/{pkid}/scene/{brb.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, play.StatusCode);
        }

        // A picture smaller than the live is fitted whole into it: its middle is the picture.
        Assert.True(await OnAirAsync(pkid, Magenta), "the picture never took the place of the canvas");
        Assert.Equal(LiveStatus.Live, Row().Status);

        using (var resume = await _host.Client.PostAsync($"/live/{pkid}/scene/resume", null))
        {
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        }

        Assert.True(await OnAirAsync(pkid, Program), "the canvas did not come back after Resume live");
        streaming.StopVideoStreamingByPkid(pkid);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Stopped), "the canvas did not stop");
    }

    private async Task<SceneButtonResponse> ButtonAsync(string label, string fileName, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var uploaded = await _host.Client.PostAsync("/scene-buttons/media?name=" + Uri.EscapeDataString(fileName), content);
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        var media = (await uploaded.Content.ReadFromJsonAsync<SceneMediaResponse>(Web))!;

        using var created = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label, mediaName = media.Name });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<SceneButtonResponse>(Web))!;
    }

    private async Task<LiveSceneState> StateAsync(int pkid) =>
        (await _host.Client.GetFromJsonAsync<LiveSceneState>($"/live/{pkid}/scene", Web))!;

    private async Task<string> RowsAsync() =>
        await _host.Client.GetStringAsync("/orbis/mainLive?handler=Rows&channelName=" + Uri.EscapeDataString("deck-channel"));

    /// <summary>Whether the middle of the picture the live is encoding turns this colour within the wait.</summary>
    private async Task<bool> OnAirAsync(int pkid, (int R, int G, int B) colour)
    {
        var frames = _host.Services.GetRequiredService<LivePreviewFrames>();
        (int R, int G, int B)? seen = null;
        var found = await WaitForAsync(async () =>
        {
            if (frames.Latest(pkid) is not { } frame)
            {
                return false;
            }

            seen = await CentreOfAsync(frame.Bytes);
            return seen is { } centre
                && Math.Abs(centre.R - colour.R) < 48 && Math.Abs(centre.G - colour.G) < 48 && Math.Abs(centre.B - colour.B) < 48;
        });

        if (!found)
        {
            _output.WriteLine($"waited for {colour}, the preview showed {seen?.ToString() ?? "nothing"}");
        }

        return found;
    }

    /// <summary>The colour of the middle of a JPEG of the preview, averaged over a few pixels.</summary>
    private static async Task<(int R, int G, int B)?> CentreOfAsync(byte[] jpeg)
    {
        using var process = Process.Start(new ProcessStartInfo(Tool("ffmpeg")!)
        {
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-f", "image2pipe", "-c:v", "mjpeg", "-i", "pipe:0",
                "-vf", "crop=8:8:(iw-8)/2:(ih-8)/2,scale=1:1", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        var reading = Task.Run(async () =>
        {
            using var pixel = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(pixel);
            return pixel.ToArray();
        });
        _ = process.StandardError.ReadToEndAsync();
        await process.StandardInput.BaseStream.WriteAsync(jpeg);
        process.StandardInput.Close();
        var bytes = await reading;
        await process.WaitForExitAsync();
        return bytes.Length == 3 ? (bytes[0], bytes[1], bytes[2]) : null;
    }

    private static VideoSettingsRequest Setting() => JsonSerializer.Deserialize<VideoSettingsRequest>(
        """
        {
          "title": "Test", "videoCodec": 27, "videoCodecName": "libx264", "pixelFormat": 0,
          "videoBitrate": 800000, "videoFormat": "flv", "gopSize": 2, "isVideoAndAudioSettingActive": true,
          "audioSettingRecord": { "audioCodec": 86018, "audioBitrate": 64000 }
        }
        """,
        Web)!;

    private static Task<bool> WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
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
