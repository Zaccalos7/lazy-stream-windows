using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// The adaptive bitrate switch of a video setting: on by default, kept in the options of the
/// setting like the low latency one, never handed to ffmpeg, and, off, a live that goes out at the
/// bitrate of the setting whatever the ladder of the live decided.
/// </summary>
public sealed class AdaptiveBitrateSwitchTests
{
    private static VideoSettingEntity Setting(string? adaptive = null)
    {
        var setting = new VideoSettingEntity
        {
            Title = "test",
            VideoCodec = 27,
            VideoCodecName = "libx264",
            PixelFormat = 0,
            VideoBitrate = 6_000_000,
            VideoFormat = "flv",
            GopSize = 2,
            AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
        };

        if (adaptive is not null)
        {
            setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity { Key = VideoSettingAdaptiveBitrate.OptionKey, Value = adaptive });
        }

        return setting;
    }

    [Theory]
    // A setting made before the switch existed went out adaptive, and still does.
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData(" Off ", false)]
    [InlineData("no", false)]
    public void OnlyAnExplicitNoTurnsItOff(string? stored, bool on) =>
        Assert.Equal(on, VideoSettingAdaptiveBitrate.IsOn(Setting(stored)));

    [Fact]
    public void SetReplacesWhatTheSettingSaidBefore()
    {
        var setting = Setting("1");

        VideoSettingAdaptiveBitrate.Set(setting, false);

        var option = Assert.Single(setting.VideoSettingsOptions, option => option.Key == VideoSettingAdaptiveBitrate.OptionKey);
        Assert.Equal("0", option.Value);
        Assert.False(VideoSettingAdaptiveBitrate.IsOn(setting));
    }

    [Fact]
    public void TheFormStartsOnAndCarriesTheSwitchBothWays()
    {
        Assert.True(new VideoSettingForm().AdaptiveBitrate);

        var off = VideoSettingForm.From(VideoSettingsRequest.FromEntity(Setting("0")));
        Assert.False(off.AdaptiveBitrate);
        Assert.Contains(off.ToRequest().VideoOptions!, option => option.Key == VideoSettingAdaptiveBitrate.OptionKey && option.Value == "0");

        // A setting saved before the switch existed opens with it on.
        Assert.True(VideoSettingForm.From(VideoSettingsRequest.FromEntity(Setting())).AdaptiveBitrate);
    }

    [Fact]
    public void TheSwitchIsADecisionOfThisApplicationNotAnOptionOfFfmpeg()
    {
        var command = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting("0"),
            Profile: StreamPlatformProfile.Twitch));

        Assert.DoesNotContain("-" + VideoSettingAdaptiveBitrate.OptionKey, command);
        Assert.Contains(VideoSettingAdaptiveBitrate.OptionKey, VideoSettingQuality.InternalKeys);
    }

    [Fact]
    public void OffTheLiveGoesOutAtTheBitrateOfTheSettingWhateverTheLadderDecided()
    {
        var output = LiveOutput.For("rtmp://live.twitch.tv/app/key", new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger.Instance)!;

        // A network that carries 4 Mbps of a 6 Mbps live: the ladder of the live caps it.
        long now = 1_000_000, media = 0, bytes = 0;
        RateDecision? decision = null;
        for (var poll = 0; poll < 120 && decision is null; poll++)
        {
            now += 500;
            var congested = poll >= 20;
            media += congested ? 325 : 500;
            bytes += (congested ? 4_000_000L : 6_160_000L) * 500 / 8000;
            decision = output.Ladder.Observe(new DeliverySample(now, media, bytes, 1000), 6_000_000, 160_000);
        }

        Assert.Equal(3_240_000, decision?.To);
        Assert.Equal(3_240_000, FfmpegStreamingSession.BitrateOf(Setting(), output));
        Assert.Equal(3_240_000, FfmpegStreamingSession.BitrateOf(Setting("1"), output));

        // Switched off: nothing overrides the setting (null is "the bitrate of the setting").
        Assert.Null(FfmpegStreamingSession.BitrateOf(Setting("0"), output));
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://live.twitch.tv/app/key", new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting("0"),
            Profile: StreamPlatformProfile.Twitch, Bitrate: FfmpegStreamingSession.BitrateOf(Setting("0"), output))));
        Assert.Contains("-b:v 6000000 -maxrate 6000000", command, StringComparison.Ordinal);
    }
}
