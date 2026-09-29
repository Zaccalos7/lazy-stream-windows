using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>
/// Real FFmpeg round trip: a generated clip is streamed by the application exactly like the
/// desktop one does, and the produced FLV plus the final video status are verified. The test is
/// skipped when ffmpeg/ffprobe are not installed, so the suite stays green everywhere.
/// </summary>
public sealed class FfmpegStreamingTests : IAsyncLifetime
{
    private TestHostRunner _host = null!;
    private readonly ITestOutputHelper _output;

    public FfmpegStreamingTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task StartLive_StreamsThePlaylistWithFfmpegAndEndsTheVideo()
    {
        var tools = new[] { Tool("ffmpeg"), Tool("ffprobe") };
        if (tools.Any(string.IsNullOrEmpty))
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the streaming test is skipped.");
            return;
        }

        var clips = Path.Combine(_host.DataDirectory, "clips");
        var output = Path.Combine(_host.DataDirectory, "output");
        Directory.CreateDirectory(clips);
        Directory.CreateDirectory(output);
        var clip = Path.Combine(clips, "clip.mp4");
        await RunAsync(
            tools[0]!,
            "-y -f lavfi -i testsrc=size=320x240:rate=30 -f lavfi -i sine=frequency=440:sample_rate=44100 "
            + "-t 2 -pix_fmt yuv420p -c:a aac " + Quote(clip));

        var probe = new FfmpegProbe(
            new FfmpegToolLocator(tools[0]!, tools[1]!),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegProbe>.Instance);
        var media = await probe.ProbeAsync(clip, CancellationToken.None);
        Assert.Equal(320, media.Width);
        Assert.Equal(240, media.Height);
        Assert.Equal(30d, media.FrameRate, 3);
        Assert.True(media.HasAudio, "the generated test clip has no audio track");

        using var started = await _host.Client.PostAsJsonAsync("/live/start-live", new
        {
            // The output url is built exactly like the RTMP one, it is only consumed by ffmpeg.
            streamUrl = new Uri(output).AbsoluteUri.TrimEnd('/'),
            streamKey = "stream.flv",
            videoPath = clips,
            platformStreamName = "channel-ffmpeg",
            channelName = "channel-ffmpeg",
            videoSettingsRecord = new
            {
                id = 1,
                title = "Twitch",
                isDefaultConfiguration = true,
                defaultPlatformConfiguration = "Twitch",
                videoCodec = 27,
                videoCodecName = "libx264",
                pixelFormat = 0,
                videoBitrate = 1_000_000,
                videoFormat = "flv",
                gopSize = 2,
                isVideoAndAudioSettingActive = true,
                audioSettingRecord = new { audioCodec = 86018, audioBitrate = 128_000 }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var status = await WaitForFinalStatusAsync();
        _output.WriteLine($"final status: {status.Status} - {status.Message}");

        Assert.Equal("ENDED", status.Status);
        var produced = new FileInfo(Path.Combine(output, "stream.flv"));
        Assert.True(produced.Exists, "ffmpeg did not produce the FLV file");
        Assert.True(produced.Length > 0, "the produced FLV file is empty");

        // The live history keeps the end message of the last streamed video.
        using var page = await _host.Client.GetAsync("/video/getPage?size=10&sort=pkid,desc");
        var content = await page.Content.ReadFromJsonAsync<JsonElement>();
        var video = content.GetProperty("content").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "clip.mp4");
        Assert.Equal("ENDED", video.GetProperty("liveStatus").GetString());
        Assert.False(video.GetProperty("shouldBeStop").GetBoolean());
    }

    private async Task<VideoStatus> WaitForFinalStatusAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var page = await _host.Client.GetAsync("/video/getPage?size=10&sort=pkid,desc");
            var content = await page.Content.ReadFromJsonAsync<JsonElement>();
            var item = content.GetProperty("content").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("name").GetString() == "clip.mp4");

            if (item.ValueKind == JsonValueKind.Object)
            {
                var status = item.GetProperty("liveStatus").GetString();
                if (status is "ENDED" or "ERROR")
                {
                    return new VideoStatus(status!, item.GetProperty("message").GetString());
                }
            }

            await Task.Delay(500);
        }

        return new VideoStatus("TIMEOUT", null);
    }

    private async Task RunAsync(string fileName, string arguments)
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

    private static string? Tool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return name switch
        {
            "ffmpeg" => SearchPath(path, "ffmpeg"),
            "ffprobe" => SearchPath(path, "ffprobe"),
            _ => null
        };
    }

    private static string? SearchPath(string path, string name)
    {
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private sealed record VideoStatus(string Status, string? Message);
}
