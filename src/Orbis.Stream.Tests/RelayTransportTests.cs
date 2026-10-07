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
}
