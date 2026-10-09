using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class RelayTransportTests
{
    private static readonly FfmpegToolLocator Locator = new("ffmpeg", "ffprobe");

    private const string YouTube = "rtmps://a.rtmps.youtube.com/live2/key";

    private const string Twitch = "rtmp://live.twitch.tv/app/key";

    [Fact]
    public void YouTubeGoesOutThroughAnFfmpegSenderThatKeepsTheTime()
    {
        var output = LiveOutput.For(YouTube, Locator, NullLogger.Instance);

        Assert.NotNull(output);
        Assert.Equal(RelayTransport.FfmpegSender, output!.Profile.Transport);
        Assert.Equal(RelayPacing.Sender, output.Profile.Pacing);
        Assert.Equal(StreamPlatform.YouTube, output.Profile.Platform);
    }

    [Fact]
    public void AYouTubeChannelWithTheSwitchOffGoesOutOnTheNativePublisherPacedByTheRelay()
    {
        // Without the ffmpeg sender, the relay is the only clock there is.
        var output = LiveOutput.For(YouTube, Locator, NullLogger.Instance, RelayTransport.NativeRtmp);

        Assert.Equal(RelayTransport.NativeRtmp, output!.Profile.Transport);
        Assert.Equal(RelayPacing.Relay, output.Profile.Pacing);
        // Everything else stays YouTube's.
        Assert.Equal(StreamPlatformProfile.YouTube.Preroll, output.Profile.Preroll);
        Assert.True(output.Profile.RequiresAudio);
        Assert.True(output.Profile.UniformFormat);
    }

    [Fact]
    public void TwitchKeepsTheRelayClockAndTheNativeRtmp()
    {
        var output = LiveOutput.For(Twitch, Locator, NullLogger.Instance);

        Assert.NotNull(output);
        Assert.Equal(RelayTransport.NativeRtmp, output!.Profile.Transport);
        Assert.Equal(RelayPacing.Relay, output.Profile.Pacing);
    }

    [Fact]
    public void TwitchHasNoSwitch()
    {
        // The switch is YouTube's: Twitch goes out the way it did before it existed.
        var output = LiveOutput.For(Twitch, Locator, NullLogger.Instance, RelayTransport.FfmpegSender);

        Assert.Equal(StreamPlatformProfile.Twitch, output!.Profile);
    }

    [Fact]
    public void OnlyYouTubeKeepsOneFormatForTheWholeConnection()
    {
        Assert.True(StreamPlatformProfile.YouTube.UniformFormat);
        Assert.False(StreamPlatformProfile.Twitch.UniformFormat);
        Assert.False(StreamPlatformProfile.Twitch.ConstantBitrate);
        Assert.False(StreamPlatformProfile.Twitch.ChoosableTransport);
    }

    [Fact]
    public void TheYouTubeSenderReadsAtRealTimeWithThePrerollAsItsBurst()
    {
        var arguments = string.Join(' ', FfmpegCommandBuilder.BuildSender(YouTube, StreamPlatformProfile.YouTube));

        Assert.Contains("-readrate 1 -readrate_initial_burst 2 -f flv -i pipe:0", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ATwitchSenderOnlyCopiesWhatTheRelayPaced()
    {
        var arguments = FfmpegCommandBuilder.BuildSender(Twitch, StreamPlatformProfile.Twitch with { Transport = RelayTransport.FfmpegSender });

        Assert.DoesNotContain("-readrate", arguments);
    }

    [Fact]
    public void ACustomIngestHasNoRelayWhateverTheChannelAsks()
    {
        Assert.Null(LiveOutput.For("rtmp://example.com/live/key", Locator, NullLogger.Instance, RelayTransport.FfmpegSender));
    }

    private const string Kick = "rtmps://fa723fc1b171.global-contribute.live-video.net/app/sk_us-west-2_key";

    private const string Facebook = "rtmps://rtmp-api.facebook.com:443/rtmp/FB-123-0-Abc";

    [Fact]
    public void KickGoesOutOnTheNativePublisherPacedByTheRelayAndHasNoSwitch()
    {
        var output = LiveOutput.For(Kick, Locator, NullLogger.Instance, RelayTransport.FfmpegSender);

        Assert.Equal(StreamPlatformProfile.Kick, output!.Profile);
        Assert.Equal(RelayTransport.NativeRtmp, output.Profile.Transport);
        Assert.Equal(RelayPacing.Relay, output.Profile.Pacing);
    }

    [Fact]
    public void FacebookGamingGoesOutThroughAnFfmpegSenderThatReadsAtRealTimeWithItsBurst()
    {
        var output = LiveOutput.For(Facebook, Locator, NullLogger.Instance, RelayTransport.NativeRtmp);

        // The channel asks for nothing on Facebook: it goes out the way its kind does, over RTMPS.
        Assert.Equal(StreamPlatformProfile.Facebook, output!.Profile);
        var arguments = string.Join(' ', FfmpegCommandBuilder.BuildSender(Facebook, output.Profile));
        Assert.Contains("-readrate 1 -readrate_initial_burst 2 -f flv -i pipe:0", arguments, StringComparison.Ordinal);
        Assert.EndsWith(Facebook, arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLiveOnARelayHasTheBitrateLadderOfItsPlatform()
    {
        var output = LiveOutput.For(Twitch, Locator, NullLogger.Instance)!;

        // Nothing measured, nothing capped: the setting is what goes out.
        Assert.Null(output.Ladder.Cap);
        Assert.Equal(6_000_000, output.Ladder.BitrateFor(6_000_000));
        Assert.Null(output.AdaptBitrate(6_000_000, 160_000));
    }
}
