using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class RelayTransportTests
{
    private static readonly FfmpegToolLocator Locator = new("ffmpeg", "ffprobe");

    private const string YouTube = "rtmps://a.rtmps.youtube.com/live2/key";

    [Fact]
    public void ALiveGoesOutOnTheTransportOfItsPlatformByDefault()
    {
        var output = LiveOutput.For(YouTube, Locator, NullLogger.Instance);

        Assert.NotNull(output);
        Assert.Equal(RelayTransport.NativeRtmp, output!.Profile.Transport);
        Assert.Equal(StreamPlatform.YouTube, output.Profile.Platform);
    }

    [Fact]
    public void AChannelThatAsksForFfmpegGetsItAndKeepsEverythingElseOfThePlatform()
    {
        var output = LiveOutput.For(YouTube, Locator, NullLogger.Instance, RelayTransport.FfmpegSender);

        Assert.NotNull(output);
        Assert.Equal(RelayTransport.FfmpegSender, output!.Profile.Transport);
        // Only the publisher changes: the pacing and the audio YouTube needs stay its own.
        Assert.Equal(StreamPlatformProfile.YouTube with { Transport = RelayTransport.FfmpegSender }, output.Profile);
    }

    [Fact]
    public void ACustomIngestHasNoRelayWhateverTheChannelAsks()
    {
        Assert.Null(LiveOutput.For("rtmp://example.com/live/key", Locator, NullLogger.Instance, RelayTransport.FfmpegSender));
    }
}
