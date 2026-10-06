using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;
using Orbis.Stream.Core.Streaming.Rtmp;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class Amf0Tests
{
    [Fact]
    public void Amf0_ReadsBackWhatItWrites()
    {
        var bytes = new Amf0Writer()
            .String("connect")
            .Number(1)
            .Object(("app", "live2"), ("tcUrl", "rtmps://a.rtmps.youtube.com/live2"), ("fpad", false), ("audioCodecs", 3191))
            .Null()
            .ToArray();

        var values = Amf0Reader.ReadAll(bytes);

        Assert.Equal(4, values.Count);
        Assert.Equal("connect", values[0]);
        Assert.Equal(1d, values[1]);
        var properties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(values[2]);
        Assert.Equal("live2", properties["app"]);
        Assert.Equal("rtmps://a.rtmps.youtube.com/live2", properties["tcUrl"]);
        Assert.Equal(false, properties["fpad"]);
        Assert.Equal(3191d, properties["audioCodecs"]);
        Assert.Null(values[3]);
    }

    [Fact]
    public void Amf0_StopsAtWhatItCannotReadInsteadOfFailing()
    {
        // A date (0x0B) after the values a publisher needs: those still come back.
        var bytes = new Amf0Writer().String("onStatus").Number(0).ToArray().Concat(new byte[] { 0x0B, 1, 2 }).ToArray();
        Assert.Equal(["onStatus", 0d], Amf0Reader.ReadAll(bytes));
    }
}

public sealed class RtmpPublisherUrlTests
{
    [Theory]
    [InlineData("http://live.twitch.tv/app/key")]
    [InlineData("rtmp://live.twitch.tv/key")]
    [InlineData("rtmp://live.twitch.tv/app/")]
    [InlineData("rtmp:///app/key")]
    public void Publisher_RefusesAUrlThatIsNotAnIngestWithAKey(string url) =>
        Assert.Throws<ArgumentException>(() =>
            new RtmpPublisher(url, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), NullLogger.Instance));

    [Fact]
    public void Publisher_FailsWithinItsTimeoutWhenNobodyAnswers()
    {
        // A port on the loopback nobody listens on: refused at once, reported as an RTMP failure.
        var port = FreePort();
        using var publisher = new RtmpPublisher(
            $"rtmp://127.0.0.1:{port}/app/key", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), NullLogger.Instance);

        var started = Stopwatch.GetTimestamp();
        Assert.ThrowsAny<Exception>(() => publisher.Open(CancellationToken.None));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.False(publisher.IsPublishing);
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// The native transport against a real RTMP server: ffmpeg in listen mode takes the publish
/// (handshake, connect, createStream, publish, chunked media) the way an ingest does, and writes
/// what it received to a file that ffprobe then reads. Skipped when ffmpeg is not installed.
/// </summary>
public sealed class RtmpPublisherFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public RtmpPublisherFfmpegTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(StreamPlatform.Twitch, 0)]
    [InlineData(StreamPlatform.YouTube, 0)]

    // Past 2^24 ms (4 h 39 min): every chunk carries the extended timestamp, as on a long live.
    [InlineData(StreamPlatform.Twitch, 16_800)]
    public async Task Native_PublishesThePacedStreamToAnRtmpServer(StreamPlatform platform, int offsetSeconds)
    {
        var ffmpeg = Which("ffmpeg");
        var ffprobe = Which("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the RTMP test is skipped.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("orbis-rtmp-");
        try
        {
            var clip = Path.Combine(directory.FullName, "clip.mp4");
            var received = Path.Combine(directory.FullName, "received.flv");
            await RunAsync(
                ffmpeg,
                "-y -f lavfi -i testsrc=size=320x240:rate=30 -f lavfi -i sine=frequency=440:sample_rate=44100 "
                + $"-t 3 -pix_fmt yuv420p -c:a aac \"{clip}\"");

            var port = RtmpPublisherUrlTests.FreePort();
            var url = $"rtmp://127.0.0.1:{port}/live2/test-key-1234";
            using var server = Process.Start(new ProcessStartInfo(
                ffmpeg, $"-hide_banner -loglevel error -listen 1 -timeout 20 -i {url} -c copy -copyts -y \"{received}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var serverError = server.StandardError.ReadToEndAsync();
            await Task.Delay(1500);

            var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
            var probe = await new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance).ProbeAsync(clip, CancellationToken.None);
            var profile = platform == StreamPlatform.YouTube ? StreamPlatformProfile.YouTube : StreamPlatformProfile.Twitch;
            var setting = new VideoSettingEntity
            {
                Title = "test",
                VideoCodec = 27,
                VideoCodecName = "libx264",
                PixelFormat = 0,
                VideoBitrate = 800_000,
                VideoFormat = "flv",
                GopSize = 2,
                AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 96_000 },
                VideoSettingsOptions = offsetSeconds > 0
                    ? [new VideoSettingsOptionEntity { Key = "output_ts_offset", Value = offsetSeconds.ToString() }]
                    : []
            };

            var started = Stopwatch.GetTimestamp();
            await using (var session = FfmpegStreamingSession.Start(
                locator, 1, clip, url, setting, probe, new OutputLogger(_output), profile: profile))
            {
                var exitCode = await session.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
                var elapsed = Stopwatch.GetElapsedTime(started);
                _output.WriteLine($"{platform}: exit {exitCode} in {elapsed.TotalSeconds:0.00}s, position {session.PositionMilliseconds} ms");
                _output.WriteLine(await session.ReadErrorAsync());

                Assert.Equal(0, exitCode);
                Assert.True(session.IsOnAir, "the publish was never accepted");
                Assert.InRange(elapsed.TotalSeconds, 3 - profile.Preroll.TotalSeconds - 0.5, 3 + 4);
                // What went on air, whatever the timestamps started from.
                Assert.InRange(session.PositionMilliseconds, 2500, 3500);
            }

            await server.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            _output.WriteLine("server: " + await serverError);

            var streams = await RunAsync(ffprobe, $"-v error -show_entries stream=codec_type -of csv=p=0 \"{received}\"");
            Assert.Contains("video", streams, StringComparison.Ordinal);
            Assert.Contains("audio", streams, StringComparison.Ordinal);

            var duration = double.Parse(
                (await RunAsync(ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{received}\"")).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 2.5, 3.5);

            if (offsetSeconds > 0)
            {
                var start = double.Parse(
                    (await RunAsync(ffprobe, $"-v error -show_entries format=start_time -of csv=p=0 \"{received}\"")).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
                _output.WriteLine($"start_time {start}");
                Assert.InRange(start, offsetSeconds - 1, offsetSeconds + 1);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string? Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);

    private static async Task<string> RunAsync(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    /// <summary>The log of the live in the output of the test, where a failure needs it.</summary>
    private sealed class OutputLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                output.WriteLine($"[{logLevel}] {formatter(state, exception)} {exception?.Message}");
            }
            catch (InvalidOperationException)
            {
                // The test is over: a late line from a relay thread has nowhere to go.
            }
        }
    }
}
