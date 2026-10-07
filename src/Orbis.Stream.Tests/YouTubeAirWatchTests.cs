using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Lookup = Orbis.Stream.Core.Streaming.LivePlatformEmbeds.LivePlatformLookup;

namespace Orbis.Stream.Tests;

public sealed class YouTubeAirWatchTests
{
    private static readonly Lookup OnAir = Lookup.Of(new LivePlatformEmbed("youtube", "channel", "https://www.youtube.com/embed/abcdefghijk"));
    private static readonly Lookup OffAir = Lookup.Nothing;
    private static readonly Lookup Unreachable = Lookup.Failed("YouTube did not return the streams page of @channel");

    private static YouTubeAirWatch Watch()
    {
        // The repositories are only read by a pass over the running lives, which these tests do
        // not make: a factory pointing at a file that is never opened is enough.
        var factory = new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), "orbis-air-watch-unused.db"));
        return new YouTubeAirWatch(
            new StreamingSessionRegistry(),
            new VideoRepository(factory),
            new VideoLiveHistoryRepository(factory),
            new LivePlatformEmbeds(new HttpClient(), NullLogger<LivePlatformEmbeds>.Instance),
            new LiveChangeNotifier(),
            NullLogger<YouTubeAirWatch>.Instance);
    }

    [Fact]
    public void ALiveIsFlaggedOnlyAfterEnoughAnswersInARowSayItIsNotOnAir()
    {
        var watch = Watch();

        // One answer is a moment: the page of the streams can lag the broadcast by a few seconds.
        Assert.False(watch.Observe(7, OffAir));
        Assert.False(watch.IsOffAir(7));

        Assert.True(watch.Observe(7, OffAir));
        Assert.True(watch.IsOffAir(7));

        // Flagged once: the next miss changes nothing the pages have to redraw.
        Assert.False(watch.Observe(7, OffAir));
    }

    [Fact]
    public void ALiveFoundOnAirClearsTheFlagAndStartsCountingAgain()
    {
        var watch = Watch();
        watch.Observe(7, OffAir);
        watch.Observe(7, OffAir);

        Assert.True(watch.Observe(7, OnAir));
        Assert.False(watch.IsOffAir(7));

        // The misses before it are gone: one more is not enough to flag it again.
        watch.Observe(7, OffAir);
        Assert.False(watch.IsOffAir(7));
    }

    [Fact]
    public void APlatformThatCouldNotBeAskedLeavesTheAnswerAsItWas()
    {
        var watch = Watch();

        // Not reachable is not off air: nothing is counted.
        watch.Observe(7, Unreachable);
        watch.Observe(7, Unreachable);
        Assert.False(watch.IsOffAir(7));

        // And a flag already up stays up until the live is really found.
        watch.Observe(7, OffAir);
        watch.Observe(7, OffAir);
        Assert.False(watch.Observe(7, Unreachable));
        Assert.True(watch.IsOffAir(7));
    }

    [Fact]
    public void ForgettingALiveDropsItsFlag()
    {
        var watch = Watch();
        watch.Observe(7, OffAir);
        watch.Observe(7, OffAir);

        Assert.True(watch.Forget(7));
        Assert.False(watch.IsOffAir(7));
        Assert.False(watch.Forget(7));
    }

    [Fact]
    public void LivesAreCountedApart()
    {
        var watch = Watch();
        watch.Observe(7, OffAir);
        watch.Observe(8, OffAir);
        watch.Observe(7, OffAir);

        Assert.True(watch.IsOffAir(7));
        Assert.False(watch.IsOffAir(8));
    }
}
