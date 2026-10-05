using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class StreamPlatformTests
{
    [Theory]
    [InlineData("rtmp://live.twitch.tv/app/key", StreamPlatform.Twitch)]
    [InlineData("rtmp://live-double.twitch.tv/app/key", StreamPlatform.Twitch)]
    [InlineData("rtmps://ingest.global-contribute.live-video.net/app/key", StreamPlatform.Twitch)]
    [InlineData("rtmps://a.rtmps.youtube.com/live2/key", StreamPlatform.YouTube)]
    [InlineData("rtmp://a.rtmp.youtube.com/live2/key", StreamPlatform.YouTube)]
    [InlineData("rtmp://my.cdn.example/live/key", StreamPlatform.Generic)]
    [InlineData("file:///tmp/output/stream.flv", StreamPlatform.Generic)]
    [InlineData("/tmp/output/stream.flv", StreamPlatform.Generic)]
    [InlineData("rtmp://notyoutube.com/live2/key", StreamPlatform.Generic)]
    [InlineData(null, StreamPlatform.Generic)]
    public void Detect_ReadsThePlatformOffTheHost(string? url, StreamPlatform expected) =>
        Assert.Equal(expected, StreamPlatforms.Detect(url));

    [Theory]
    [InlineData("rtmps://a.rtmp.youtube.com/live2/abcd-efgh", "rtmps://a.rtmps.youtube.com/live2/abcd-efgh")]
    [InlineData("rtmps://b.rtmp.youtube.com:443/live2/abcd-efgh", "rtmps://b.rtmps.youtube.com:443/live2/abcd-efgh")]
    [InlineData("rtmps://a.rtmps.youtube.com/live2/abcd-efgh", "rtmps://a.rtmps.youtube.com/live2/abcd-efgh")]
    [InlineData("rtmp://a.rtmp.youtube.com/live2/abcd-efgh", "rtmp://a.rtmp.youtube.com/live2/abcd-efgh")]
    [InlineData("rtmp://live.twitch.tv/app/key", "rtmp://live.twitch.tv/app/key")]
    public void NormalizeIngestUrl_PointsRtmpsAtTheRtmpsHostOfYouTube(string url, string expected) =>
        Assert.Equal(expected, StreamPlatforms.NormalizeIngestUrl(url));

    [Fact]
    public void For_GivesEveryPlatformItsProfile()
    {
        Assert.Same(StreamPlatformProfile.Twitch, StreamPlatformProfile.For("rtmp://live.twitch.tv/app/key"));
        Assert.Same(StreamPlatformProfile.YouTube, StreamPlatformProfile.For("rtmps://a.rtmps.youtube.com/live2/key"));
        Assert.Same(StreamPlatformProfile.Generic, StreamPlatformProfile.For("rtmp://my.cdn.example/live/key"));

        // YouTube is the one that is pushed: a head start, a deeper buffer and a sound it can trust.
        Assert.True(StreamPlatformProfile.YouTube.Preroll > StreamPlatformProfile.Twitch.Preroll);
        Assert.True(StreamPlatformProfile.YouTube.MaxLead > StreamPlatformProfile.Twitch.MaxLead);
        Assert.True(StreamPlatformProfile.YouTube.RequiresAudio);
        Assert.False(StreamPlatformProfile.Twitch.RequiresAudio);
        Assert.False(StreamPlatformProfile.Generic.UsesRelay);
    }
}

public sealed class HybridWaiterTests
{
    [Fact]
    public void WaitUntil_WakesOnTimeAndNotBefore()
    {
        using var waiter = HybridWaiter.Create();
        var lateness = new List<double>();

        foreach (var milliseconds in new[] { 3, 17, 40, 3, 17, 40, 3, 17, 40 })
        {
            var start = Stopwatch.GetTimestamp();
            var target = start + HybridWaiter.Ticks(TimeSpan.FromMilliseconds(milliseconds));
            waiter.WaitUntil(target, CancellationToken.None);
            var now = Stopwatch.GetTimestamp();

            // Never early: that is the one thing the spin at the end guarantees whatever the load.
            Assert.True(now >= target, $"woke {Stopwatch.GetElapsedTime(now, target).TotalMilliseconds} ms early");
            lateness.Add(Stopwatch.GetElapsedTime(target, now).TotalMilliseconds);
        }

        // On time is a matter of the median: the suite runs ffmpeg next to this test and the
        // scheduler may take the core away once, but a wait that sleeps through its target (the
        // oversleep the halving is there to avoid) is late every time.
        lateness.Sort();
        Assert.True(lateness[lateness.Count / 2] < 2, $"median lateness {lateness[lateness.Count / 2]:0.###} ms");
    }

    [Fact]
    public void WaitUntil_ReturnsAtOnceForATargetInThePast()
    {
        using var waiter = HybridWaiter.Create();
        var start = Stopwatch.GetTimestamp();
        waiter.WaitUntil(start - HybridWaiter.Ticks(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void WaitUntil_HonoursCancellation()
    {
        using var waiter = HybridWaiter.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var start = Stopwatch.GetTimestamp();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            waiter.WaitUntil(start + HybridWaiter.Ticks(TimeSpan.FromSeconds(10)), cancellation.Token));
        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2));
    }
}

public sealed class FlvPacedRelayTests
{
    private static readonly StreamPlatformProfile Paced = StreamPlatformProfile.Twitch;

    [Fact]
    public async Task Relay_ForwardsEveryByteUnchanged()
    {
        var flv = Flv([0, 33, 66, 100, 133]);
        var destination = new MemoryStream();

        var relay = new FlvPacedRelay(new MemoryStream(flv), destination, Paced, NullLogger.Instance);
        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(flv, destination.ToArray());
        Assert.Equal(133, relay.SentTimestampMilliseconds);
    }

    [Fact]
    public async Task Relay_SendsEveryTagWhenItsTimestampIsDue()
    {
        var destination = new TimedStream();
        var relay = new FlvPacedRelay(new MemoryStream(Flv([0, 100, 200, 300, 400])), destination, Paced, NullLogger.Instance);

        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // The header goes out first, then one write per tag: each tag leaves at start + pts.
        var tags = destination.Writes.Skip(1).ToList();
        Assert.Equal(5, tags.Count);
        for (var index = 1; index < tags.Count; index++)
        {
            var since = Stopwatch.GetElapsedTime(tags[0], tags[index]).TotalMilliseconds;
            Assert.InRange(since, index * 100 - 1, index * 100 + 40);
        }
    }

    [Fact]
    public async Task Relay_SendsThePrerollAtOnce()
    {
        var profile = Paced with { Preroll = TimeSpan.FromSeconds(2), MaxLead = TimeSpan.FromSeconds(4) };
        var start = Stopwatch.GetTimestamp();
        var relay = new FlvPacedRelay(
            new MemoryStream(Flv([0, 500, 1000, 1500, 1900, 2300])), new MemoryStream(), profile, NullLogger.Instance);

        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // Everything stamped before two seconds is a burst; the last tag waits for its 300 ms.
        Assert.InRange(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 280, 1000);
    }

    [Fact]
    public async Task Relay_WinsTheGapBackInsteadOfWritingItOff()
    {
        // A jitter buffer of one second and a stall nearly twice that long: the old code gave up
        // and moved its clock on anything past MaxCatchUp, which is where the gap used to vanish.
        var profile = Paced with { MaxLead = TimeSpan.FromSeconds(1) };

        // Writes one and two are the header and the first tag, so the stall lands in the middle.
        var destination = new TimedStream { StallOnWrite = 3, Stall = TimeSpan.FromMilliseconds(1800) };
        var relay = new FlvPacedRelay(
            new MemoryStream(Flv([0, 50, 100, 150, 200, 250, 300])), destination, profile, NullLogger.Instance);

        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // A slow network is a gap to win back, not a jump of the timestamps: the clock stays put.
        Assert.Equal(0, relay.Rebases);
        Assert.True(relay.CatchingUp >= 1, $"the relay reported {relay.CatchingUp} catch-ups");
    }

    [Fact]
    public async Task Relay_SendsFasterThanRealTimeWhileItIsBehind()
    {
        // Ten seconds of stream in tags a tenth of a second apart, stalled once for a second half
        // way through: enough queued behind the stall for the catch-up to have somewhere to go.
        var stamps = Enumerable.Range(0, 101).Select(index => index * 100).ToArray();
        var destination = new TimedStream { StallOnWrite = 52, Stall = TimeSpan.FromSeconds(1) };
        var relay = new FlvPacedRelay(
            new MemoryStream(Flv(stamps)), destination, Paced with { MaxLead = TimeSpan.FromSeconds(4) }, NullLogger.Instance);

        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        var tags = destination.Writes.Skip(1).ToList();
        Assert.Equal(stamps.Length, tags.Count);

        // The stream is ten seconds long and the stall cost a second of it. Winning the second
        // back means finishing in about ten seconds; writing it off means finishing in about
        // eleven, and the ingest plays out a second it never received.
        var total = Stopwatch.GetElapsedTime(tags[0], tags[^1]).TotalMilliseconds;
        Assert.True(total < 10_400, $"the stream took {total:0} ms of wall clock for 10_000 ms of video");
        Assert.Equal(0, relay.Rebases);
    }

    [Fact]
    public async Task Relay_FailsWhenTheSenderIsGone()
    {
        var relay = new FlvPacedRelay(
            new MemoryStream(Flv([0, 10, 20])), new BrokenStream(), Paced, NullLogger.Instance);

        relay.Start();

        await Assert.ThrowsAsync<IOException>(() => relay.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Relay_RefusesAStreamThatIsNotFlv()
    {
        var destination = new MemoryStream();
        var relay = new FlvPacedRelay(
            new MemoryStream("not an flv at all"u8.ToArray()), destination, Paced, NullLogger.Instance);

        relay.Start();
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(destination.ToArray());
    }

    /// <summary>An FLV header and one video tag per timestamp, with a payload to tell them apart.</summary>
    private static byte[] Flv(IReadOnlyList<int> timestamps)
    {
        var bytes = new List<byte> { (byte)'F', (byte)'L', (byte)'V', 1, 5, 0, 0, 0, 9, 0, 0, 0, 0 };
        foreach (var timestamp in timestamps)
        {
            var data = Enumerable.Range(0, 32).Select(index => (byte)(index + timestamp)).ToArray();
            bytes.Add(9);
            bytes.AddRange([(byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            bytes.AddRange([(byte)(timestamp >> 16), (byte)(timestamp >> 8), (byte)timestamp, (byte)(timestamp >> 24)]);
            bytes.AddRange([0, 0, 0]);
            bytes.AddRange(data);
            var previous = 11 + data.Length;
            bytes.AddRange([(byte)(previous >> 24), (byte)(previous >> 16), (byte)(previous >> 8), (byte)previous]);
        }

        return [.. bytes];
    }

    /// <summary>Records when every write happened, and can stall one of them.</summary>
    private sealed class TimedStream : MemoryStream
    {
        public List<long> Writes { get; } = [];

        public int StallOnWrite { get; init; } = -1;

        public TimeSpan Stall { get; init; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes.Add(Stopwatch.GetTimestamp());
            if (Writes.Count == StallOnWrite)
            {
                Thread.Sleep(Stall);
            }

            base.Write(buffer);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Broken pipe");
    }
}

public sealed class RelayCommandTests
{
    private static readonly MediaProbeResult Silent = new(1280, 720, 30, false, 0, 10);

    private static VideoSettingEntity Setting() => new()
    {
        Title = "test",
        Id = 1,
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 4_500_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    [Fact]
    public void Relay_EncoderWritesFlvOnItsOutputAndLeavesThePacingToTheRelay()
    {
        var arguments = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", Silent, Setting(), Profile: StreamPlatformProfile.Twitch));
        var text = string.Join(' ', arguments);

        Assert.DoesNotContain("-readrate", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-progress", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rtmp://", text, StringComparison.Ordinal);
        Assert.Equal("pipe:1", arguments[^1]);
        Assert.Contains("-f flv", text, StringComparison.Ordinal);
        Assert.Contains("-flush_packets 1", text, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*2)", text, StringComparison.Ordinal);

        // Twitch does not need a sound: a silent file stays silent.
        Assert.DoesNotContain("anullsrc", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData(null, true)]
    public void LowLatencySwitch_KeepsTheEncoderDefaultsOnlyWhenItIsOn(string? stored, bool expected)
    {
        var setting = Setting();
        if (stored is not null)
        {
            setting.VideoSettingsOptions =
            [
                new VideoSettingsOptionEntity
                {
                    Key = VideoSettingLatency.OptionKey,
                    Value = stored
                }
            ];
        }

        var text = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", Silent, setting,
            Profile: StreamPlatformProfile.Twitch)));

        foreach (var flag in new[] { "-tune zerolatency", "scenecut=0:rc_lookahead=0" })
        {
            if (expected)
            {
                Assert.Contains(flag, text, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(flag, text, StringComparison.Ordinal);
            }
        }

        // What is not low latency either way: the preset is about the CPU, not the delay, and the
        // switch is not allowed to quietly re-encode the machine's weakest encoder.
        Assert.Contains("-preset ultrafast", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LowLatencySwitch_ReachesTheHardwareEncoderToo()
    {
        var setting = Setting();
        setting.VideoCodecName = "h264_nvenc";
        setting.VideoSettingsOptions =
        [
            new VideoSettingsOptionEntity
            {
                Key = VideoSettingLatency.OptionKey,
                Value = "0"
            }
        ];

        var text = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", Silent, setting,
            Profile: StreamPlatformProfile.Twitch)));

        Assert.DoesNotContain("-delay 0", text, StringComparison.Ordinal);

        // The tune the switch took away is NVENC's "ll", and nothing else pairs a -tune here: the
        // x264 tune is not in this command line at all.
        Assert.DoesNotContain("-tune ll", text, StringComparison.Ordinal);

        // The rate control is what a viewer sees as quality, so it is untouched by the switch.
        Assert.Contains("-rc cbr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_YouTubeGetsASilentTrackWhenTheFileHasNoSound()
    {
        var arguments = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmps://a.rtmps.youtube.com/live2/key", Silent, Setting(), Profile: StreamPlatformProfile.YouTube));
        var text = string.Join(' ', arguments);

        Assert.Contains("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000", text, StringComparison.Ordinal);
        Assert.Contains("-map 1:a:0", text, StringComparison.Ordinal);
        Assert.Contains("-c:a aac", text, StringComparison.Ordinal);
        Assert.Contains("-ar 48000", text, StringComparison.Ordinal);
        Assert.Contains("-ac 2", text, StringComparison.Ordinal);
        Assert.Contains("-shortest", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relay_YouTubeSilenceFollowsTheLastSourceOfACanvas()
    {
        var items = new[]
        {
            new FfmpegCompositionItem(SourceKind.Screen, "desktop", 0, 0, 1280, 720, AudioEnabled: false)
        };
        var arguments = FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            items, "rtmps://a.rtmps.youtube.com/live2/key", Setting(), 1280, 720, 30, Profile: StreamPlatformProfile.YouTube));
        var text = string.Join(' ', arguments);

        Assert.Contains("anullsrc", text, StringComparison.Ordinal);
        Assert.Contains("-map 1:a:0", text, StringComparison.Ordinal);
        Assert.Equal("pipe:1", arguments[^1]);
    }

    [Fact]
    public void Direct_WithoutAProfileTheCommandIsTheOneOfBefore()
    {
        var arguments = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://my.cdn.example/live/key", Silent, Setting()));
        var text = string.Join(' ', arguments);

        Assert.Contains("-readrate 1", text, StringComparison.Ordinal);
        Assert.Contains("-progress pipe:1", text, StringComparison.Ordinal);
        Assert.Equal("rtmp://my.cdn.example/live/key", arguments[^1]);
        Assert.DoesNotContain("anullsrc", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-force_key_frames", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_CopiesThePacedStreamToTheIngestWithATimeout()
    {
        var arguments = FfmpegCommandBuilder.BuildSender("rtmps://a.rtmps.youtube.com/live2/key", StreamPlatformProfile.YouTube);
        var text = string.Join(' ', arguments);

        Assert.Contains("-f flv -i pipe:0", text, StringComparison.Ordinal);
        Assert.Contains("-c copy", text, StringComparison.Ordinal);
        Assert.Contains("-progress pipe:1", text, StringComparison.Ordinal);
        Assert.Contains("-rw_timeout 30000000", text, StringComparison.Ordinal);
        Assert.Equal("rtmps://a.rtmps.youtube.com/live2/key", arguments[^1]);
    }
}

/// <summary>
/// The relay with a real ffmpeg on both ends: a clip with no sound goes through the YouTube
/// profile to an FLV on disk. Skipped when ffmpeg is not installed, like the other ffmpeg tests.
/// </summary>
public sealed class PacedRelayFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public PacedRelayFfmpegTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(StreamPlatform.Twitch)]
    [InlineData(StreamPlatform.YouTube)]
    public async Task Relay_StreamsAFileInRealTimeAndIsOnAir(StreamPlatform platform)
    {
        var ffmpeg = Which("ffmpeg");
        var ffprobe = Which("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the relay test is skipped.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("orbis-relay-");
        try
        {
            var clip = Path.Combine(directory.FullName, "clip.mp4");
            var output = Path.Combine(directory.FullName, "out.flv");
            await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=320x240:rate=30 -t 4 -pix_fmt yuv420p \"{clip}\"");

            var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
            var probe = await new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance).ProbeAsync(clip, CancellationToken.None);
            // A file is not an RTMP ingest: the paced stream goes through the ffmpeg sender here,
            // and the native transport has its own test against a real RTMP server below.
            var profile = (platform == StreamPlatform.YouTube ? StreamPlatformProfile.YouTube : StreamPlatformProfile.Twitch)
                with { Transport = RelayTransport.FfmpegSender };
            var setting = new VideoSettingEntity
            {
                Title = "test",
                VideoCodec = 27,
                VideoCodecName = "libx264",
                PixelFormat = 0,
                VideoBitrate = 800_000,
                VideoFormat = "flv",
                GopSize = 2,
                AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 96_000 }
            };

            var started = Stopwatch.GetTimestamp();
            await using var session = FfmpegStreamingSession.Start(
                locator, 1, clip, output, setting, probe, NullLogger.Instance, profile: profile);

            var exitCode = await session.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            var elapsed = Stopwatch.GetElapsedTime(started);
            _output.WriteLine($"{platform}: exit {exitCode} in {elapsed.TotalSeconds:0.00}s, position {session.PositionMilliseconds} ms");
            _output.WriteLine(await session.ReadErrorAsync());

            Assert.Equal(0, exitCode);
            Assert.True(session.IsOnAir, "the sender never reported bytes on air");
            Assert.InRange(session.PositionMilliseconds, 3500, 4500);

            // Paced: four seconds of video take four seconds, less the preroll that goes out at once.
            Assert.InRange(elapsed.TotalSeconds, 4 - profile.Preroll.TotalSeconds - 0.5, 4 + 3);

            var streams = await RunAsync(
                ffprobe, $"-v error -show_entries stream=codec_type -of csv=p=0 \"{output}\"");
            Assert.Contains("video", streams, StringComparison.Ordinal);
            Assert.Equal(platform == StreamPlatform.YouTube, streams.Contains("audio", StringComparison.Ordinal));
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
}
