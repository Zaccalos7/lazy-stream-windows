using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// One picture for the whole live: on a connection shared by every video, spot and pass, the first
/// fixes the size and the rate and the ones after it are fitted into it. An ingest is not told the
/// format changed halfway, and YouTube stops processing a stream that does.
/// </summary>
public sealed class LiveFrameTests
{
    private static readonly MediaOutput FullHd = new(1920, 1080, 30d);

    private static VideoSettingEntity Setting(int? width = null, int? height = null) => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 6_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        VideoWidth = width,
        VideoHeight = height,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    private static string Command(MediaProbeResult probe, MediaOutput? frame, VideoSettingEntity? setting = null) =>
        string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmps://a.rtmps.youtube.com/live2/key", probe, setting ?? Setting(),
            Profile: StreamPlatformProfile.YouTube, Frame: frame)));

    [Fact]
    public void ASpotOfAnotherSizeIsFittedIntoThePictureOfTheLive()
    {
        // The short from the log: 432x208 at 29.95 on a live that went on air at 1080p30.
        var spot = new MediaProbeResult(432, 208, 29.95, true, 1, 30d);

        var command = Command(spot, FullHd);

        Assert.Contains(
            "-vf scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1",
            command,
            StringComparison.Ordinal);
        Assert.Contains("-r 30 ", command, StringComparison.Ordinal);
        // A mono spot after a stereo video is a change of format too.
        Assert.Contains("-ac 2", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AVideoOfTheSizeOfTheLiveIsNotScaled()
    {
        var command = Command(new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), FullHd);

        Assert.DoesNotContain("-vf ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveWithTheConnectionToItselfKeepsTheSizeOfTheFile()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://example.com/live/key", new MediaProbeResult(432, 208, 30d, true, 2, 30d), Setting())));

        Assert.DoesNotContain("-vf ", command, StringComparison.Ordinal);
    }

    [Theory]
    // A landscape video of at least 720p fixes the live at its own size.
    [InlineData(1920, 1080, null, null, 1920, 1080)]
    [InlineData(1280, 720, null, null, 1280, 720)]
    // A short in portrait, or a small clip, that happens to come first starts the live at 1080p.
    [InlineData(432, 768, null, null, 1920, 1080)]
    [InlineData(640, 360, null, null, 1920, 1080)]
    // A resolution the setting names is the live's, whatever comes first.
    [InlineData(1280, 720, 1280, 720, 1280, 720)]
    public void TheFirstVideoFixesThePictureOfTheLive(int width, int height, int? settingWidth, int? settingHeight, int liveWidth, int liveHeight)
    {
        var first = new MediaOutput(width, height, 30d);

        var frame = FfmpegCommandBuilder.FrameOfLive(Setting(settingWidth, settingHeight), first);

        Assert.Equal(new MediaOutput(liveWidth, liveHeight, 30d), frame);
    }

    [Fact]
    public void TwitchKeepsEveryFileAtItsOwnSizeAndSound()
    {
        // A mono short on a Twitch live: as it was before the connection kept one format for YouTube.
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", new MediaProbeResult(432, 208, 29.95, true, 1, 30d), Setting(),
            Profile: StreamPlatformProfile.Twitch)));

        Assert.DoesNotContain("-vf ", command, StringComparison.Ordinal);
        Assert.Contains("-ac 1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstEncoderOnAConnectionFixesItsPicture()
    {
        var output = LiveOutput.For("rtmps://a.rtmps.youtube.com/live2/key", new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger.Instance)!;

        Assert.Null(output.Frame);
        Assert.Equal(FullHd, output.Pin(FullHd));
        Assert.Equal(FullHd, output.Pin(new MediaOutput(432, 208, 29.95)));
        Assert.Equal(FullHd, output.Frame);
    }

    [Fact]
    public void ACanvasOnAConnectionOfAnotherSizeIsFittedToo()
    {
        var command = FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1280, 720, false)],
            "rtmps://a.rtmps.youtube.com/live2/key",
            Setting(),
            1280,
            720,
            30d,
            Profile: StreamPlatformProfile.YouTube,
            Frame: FullHd));

        var text = string.Join(' ', command);

        Assert.Contains("[orbisv]scale=1920:1080:force_original_aspect_ratio=decrease", text, StringComparison.Ordinal);
        Assert.Contains("-map [orbisvfit]", text, StringComparison.Ordinal);
    }

    private static readonly MediaOutput Upright = new(1080, 1920, 30d);

    [Fact]
    public void APortraitLiveIsPreviewedUpright()
    {
        // A live that goes out standing up (a 1080x1920 setting, a phone video kept at its size):
        // the preview is bounded on its height, 360x640 - the pixels of the landscape one, turned -
        // instead of a 640x1138 picture three times as heavy for a stage that shows it smaller.
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://example.com/live/key", new MediaProbeResult(1080, 1920, 30d, true, 2, 60d), Setting(),
            PreviewPath: "/tmp/preview.jpg")));

        Assert.Contains("fps=30,scale=w=-2:h='min(640,ih)'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("scale=w='min(640,iw)'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void APortraitCanvasIsPreviewedUprightToo()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1080, 1920, false)],
            "rtmps://a.rtmps.youtube.com/live2/key",
            Setting(),
            1080,
            1920,
            30d,
            PreviewPath: "/tmp/preview.jpg",
            Profile: StreamPlatformProfile.YouTube,
            Frame: Upright)));

        Assert.Contains("[orbisp0]fps=30,scale=w=-2:h='min(640,ih)'[orbisp]", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ALandscapeLiveKeepsItsPreviewBoundedOnItsWidth()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmps://a.rtmps.youtube.com/live2/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting(),
            PreviewPath: "/tmp/preview.jpg", Profile: StreamPlatformProfile.YouTube, Frame: FullHd)));

        Assert.Contains("fps=30,scale=w='min(640,iw)':h=-2", command, StringComparison.Ordinal);
    }
}

/// <summary>
/// YouTube measures what arrives against the bitrate the stream announced: a still or letterboxed
/// picture under a capped bitrate arrives at a few kilobits, and YouTube reports that it is not
/// receiving enough video. It gets true CBR; Twitch keeps the variable rate it takes well.
/// </summary>
public sealed class ConstantBitrateTests
{
    private static VideoSettingEntity Setting(string encoder = "libx264") => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = encoder,
        PixelFormat = 0,
        VideoBitrate = 6_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
        VideoSettingsOptions = [new VideoSettingsOptionEntity { Key = VideoSettingLatency.OptionKey, Value = "1" }]
    };

    private static string Command(StreamPlatformProfile profile, VideoSettingEntity? setting = null) =>
        string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmps://ingest/live2/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d),
            setting ?? Setting(), Profile: profile)));

    [Fact]
    public void YouTubeGetsTheBitrateOfTheSettingAllTheTime()
    {
        var command = Command(StreamPlatformProfile.YouTube);

        Assert.Contains("-b:v 6000000 -maxrate 6000000 -minrate 6000000", command, StringComparison.Ordinal);
        // The low latency options and the filler travel together in the one -x264-params.
        Assert.Contains("-x264-params scenecut=0:rc_lookahead=0:nal-hrd=cbr:force-cfr=1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TwitchKeepsTheVariableRate()
    {
        var command = Command(StreamPlatformProfile.Twitch);

        Assert.DoesNotContain("-minrate", command, StringComparison.Ordinal);
        Assert.DoesNotContain("nal-hrd", command, StringComparison.Ordinal);
    }

    [Fact]
    public void X264ParamsTypedByHandGetTheConstantRateMergedIn()
    {
        // The old YouTube defaults carried x264-params of their own, and the live went out capped.
        var setting = Setting();
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity { Key = "x264-params", Value = "rc_lookahead=20" });

        var command = Command(StreamPlatformProfile.YouTube, setting);

        Assert.Contains("-x264-params rc_lookahead=20:nal-hrd=cbr:force-cfr=1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyTheSettingNamesKeepsItsValue()
    {
        var setting = Setting();
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity { Key = "x264-params", Value = "nal-hrd=vbr" });

        var command = Command(StreamPlatformProfile.YouTube, setting);

        Assert.Contains("-x264-params nal-hrd=vbr:force-cfr=1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TwitchLeavesX264ParamsAsTyped()
    {
        var setting = Setting();
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity { Key = "x264-params", Value = "keyint=60" });

        var command = Command(StreamPlatformProfile.Twitch, setting);

        Assert.DoesNotContain("nal-hrd", command, StringComparison.Ordinal);
        Assert.Contains("-x264-params keyint=60", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AmfIsAskedToPadToo()
    {
        var command = Command(StreamPlatformProfile.YouTube, Setting("h264_amf"));

        Assert.Contains("-filler_data 1", command, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void APlatformBoundsTheRateControlToOneSecondOfTheBitrate(string platform)
    {
        // A keyframe may spend one second of the bitrate, not two: the peak every frame behind it
        // waits on the wire for.
        var command = Command(ProfileOf(platform));

        Assert.Contains("-maxrate 6000000", command, StringComparison.Ordinal);
        Assert.Contains("-bufsize 6000000", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomIngestKeepsTheRateControlItHad()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://example.com/live/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting())));

        Assert.Contains("-bufsize 12000000", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBitrateTheNetworkCarriesTakesThePlaceOfTheSetting()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmps://ingest/live2/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting(),
            Profile: StreamPlatformProfile.YouTube, Bitrate: 3_240_000)));

        // Still constant - at the rate the network carries.
        Assert.Contains("-b:v 3240000 -maxrate 3240000 -minrate 3240000 -bufsize 3240000", command, StringComparison.Ordinal);
        Assert.Contains("nal-hrd=cbr", command, StringComparison.Ordinal);
        Assert.DoesNotContain("6000000", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ACanvasGoesOutAtTheBitrateTheNetworkCarriesToo()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1280, 720, false)],
            "rtmp://live.twitch.tv/app/key",
            Setting(),
            1280,
            720,
            30d,
            Profile: StreamPlatformProfile.Twitch,
            Bitrate: 2_500_000)));

        Assert.Contains("-b:v 2500000 -maxrate 2500000 -bufsize 2500000", command, StringComparison.Ordinal);
    }

    public static TheoryData<string> Platforms => ["twitch", "youtube", "kick", "facebook"];

    private static StreamPlatformProfile ProfileOf(string platform) => platform switch
    {
        "twitch" => StreamPlatformProfile.Twitch,
        "youtube" => StreamPlatformProfile.YouTube,
        "kick" => StreamPlatformProfile.Kick,
        _ => StreamPlatformProfile.Facebook
    };
}
