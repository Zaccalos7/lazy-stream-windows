using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class FfmpegCommandBuilderTests
{
    private static VideoSettingEntity Setting() => new()
    {
        Id = 1,
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 5_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
        VideoSettingsOptions =
        [
            new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
            new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" }
        ]
    };

    private static MediaProbeResult Probe(bool hasAudio = true, double frameRate = 30d) =>
        new(1920, 1080, frameRate, hasAudio, hasAudio ? 2 : 0, 120d);

    [Fact]
    public void Build_UsesTheVideoSettingAsEncoderConfiguration()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), Setting())));

        Assert.Contains("-re ", command, StringComparison.Ordinal);
        Assert.Contains("-i /videos/clip.mp4", command, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", command, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt yuv420p", command, StringComparison.Ordinal);
        Assert.Contains("-f flv", command, StringComparison.Ordinal);
        Assert.Contains("-b:v 5000000", command, StringComparison.Ordinal);
        Assert.Contains("-c:a aac", command, StringComparison.Ordinal);
        Assert.Contains("-b:a 128000", command, StringComparison.Ordinal);
        Assert.Contains("-ar 44100", command, StringComparison.Ordinal);
        Assert.Contains("-ac 2", command, StringComparison.Ordinal);
        Assert.Contains("-preset ultrafast", command, StringComparison.Ordinal);
        Assert.Contains("-tune zerolatency", command, StringComparison.Ordinal);
        Assert.EndsWith(" rtmp://ingest/live/key", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30d, 2, 60)]
    [InlineData(29.97d, 2, 59)]
    [InlineData(25d, 3, 75)]
    public void Build_ComputesGopAsFrameRateTimesGopSize(double frameRate, int gopSize, int expectedGop)
    {
        var setting = Setting();
        setting.GopSize = gopSize;

        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(frameRate: frameRate), setting)));

        Assert.Contains($"-g {expectedGop}", command, StringComparison.Ordinal);
        Assert.Contains($"-r {frameRate.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SkipsAudioFlagsWhenTheInputHasNoAudioTrack()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(hasAudio: false), Setting())));

        Assert.Contains("-map 0:v:0", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:a", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-map 0:a:0?", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\PS4 live di ""me"".mp4")]
    [InlineData(@"C:\Video\")]
    public void Build_PassesTheInputPathAsOneVerbatimArgument(string inputPath)
    {
        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest(inputPath, "rtmp://ingest/live/key", Probe(), Setting()));

        Assert.Equal(inputPath, arguments[arguments.ToList().IndexOf("-i") + 1]);
    }

    [Theory]
    [InlineData("rtmp://ingest/live", "key", "rtmp://ingest/live/key")]
    [InlineData("rtmp://ingest/live/", "key", "rtmp://ingest/live/key")]
    public void BuildStreamingUrl_JoinsWithASingleSeparator(string url, string key, string expected)
    {
        Assert.Equal(expected, FfmpegCommandBuilder.BuildStreamingUrl(url, key));
    }

    [Fact]
    public void Build_ScalesToTheResolutionOfTheSetting()
    {
        var setting = Setting();
        setting.VideoWidth = 1280;
        setting.VideoHeight = 720;

        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), setting));

        Assert.Equal("scale=1280:720", arguments[arguments.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Build_RoundsTheScaleDownToAnEvenFrameSize()
    {
        var setting = Setting();
        setting.VideoWidth = 1281;
        setting.VideoHeight = 721;

        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), setting));

        Assert.Equal("scale=1280:720", arguments[arguments.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Build_LeavesTheResolutionOfTheSourceAloneWhenTheSettingHasNone()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), Setting())));

        Assert.DoesNotContain("-vf", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TakesTheFrameRateOfTheSettingOverTheOneOfTheSource()
    {
        var setting = Setting();
        setting.FrameRate = 60d;

        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(frameRate: 23.976d), setting)));

        Assert.Contains("-r 60", command, StringComparison.Ordinal);
        Assert.Contains("-g 120", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOutput_FallsBackToWhatTheSourceCarries()
    {
        var output = FfmpegCommandBuilder.ResolveOutput(Setting(), Probe(frameRate: 23.976d));

        Assert.Equal(1920, output.Width);
        Assert.Equal(1080, output.Height);
        Assert.Equal(23.976d, output.FrameRate);
    }

    [Fact]
    public void ResolveOutput_AnswersWhatTheCommandLineAsksFor()
    {
        var setting = Setting();
        setting.VideoWidth = 854;
        setting.VideoHeight = 481;
        setting.FrameRate = 24d;

        var output = FfmpegCommandBuilder.ResolveOutput(setting, Probe(frameRate: 60d));

        Assert.Equal(854, output.Width);
        Assert.Equal(480, output.Height);
        Assert.Equal(24d, output.FrameRate);
        Assert.Equal("scale=854:480", FfmpegCommandBuilder.ScaleFilter(setting));
    }
}
