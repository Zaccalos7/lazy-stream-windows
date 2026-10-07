using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Lookup = Orbis.Stream.Core.Streaming.LivePlatformEmbeds.LivePlatformLookup;

namespace Orbis.Stream.Tests;

public sealed class YouTubeAirWatchTests
{
    private static Lookup OnAir(string videoId = "abcdefghijk") =>
        Lookup.Of(new LivePlatformEmbed("youtube", "channel", $"https://www.youtube.com/embed/{videoId}", videoId));

    private static readonly Lookup OffAir = Lookup.Nothing;
    private static readonly Lookup Unreachable = Lookup.Failed("YouTube did not return the streams page of @channel");

    [Fact]
    public void ALiveIsFlaggedOnlyAfterEnoughAnswersInARowSayItIsNotOnAir()
    {
        var state = new YouTubeAirState();

        // One answer is a moment: the page of the streams can lag the broadcast by a few seconds.
        Assert.False(state.Observe(7, OffAir));
        Assert.False(state.IsOffAir(7));

        Assert.True(state.Observe(7, OffAir));
        Assert.True(state.IsOffAir(7));

        // Flagged once: the next miss changes nothing the pages have to redraw.
        Assert.False(state.Observe(7, OffAir));
    }

    [Fact]
    public void ALiveFoundOnAirClearsTheFlagAndStartsCountingAgain()
    {
        var state = new YouTubeAirState();
        state.Observe(7, OffAir);
        state.Observe(7, OffAir);

        Assert.True(state.Observe(7, OnAir()));
        Assert.False(state.IsOffAir(7));

        // The misses before it are gone: one more is not enough to flag it again.
        state.Observe(7, OffAir);
        Assert.False(state.IsOffAir(7));
    }

    [Fact]
    public void APlatformThatCouldNotBeAskedLeavesTheAnswerAsItWas()
    {
        var state = new YouTubeAirState();

        // Not reachable is not off air: nothing is counted.
        state.Observe(7, Unreachable);
        state.Observe(7, Unreachable);
        Assert.False(state.IsOffAir(7));

        // And a flag already up stays up until the live is really found.
        state.Observe(7, OffAir);
        state.Observe(7, OffAir);
        Assert.False(state.Observe(7, Unreachable));
        Assert.True(state.IsOffAir(7));
    }

    [Fact]
    public void ForgettingALiveDropsItsFlag()
    {
        var state = new YouTubeAirState();
        state.Observe(7, OffAir);
        state.Observe(7, OffAir);

        Assert.True(state.Forget(7));
        Assert.False(state.IsOffAir(7));
        Assert.False(state.Forget(7));
    }

    [Fact]
    public void LivesAreCountedApartAndARowSeesTheLivesOfItsHistory()
    {
        var state = new YouTubeAirState();
        state.BelongsTo(7, 100);
        state.BelongsTo(8, 200);
        state.Observe(7, OffAir);
        state.Observe(8, OffAir);
        state.Observe(7, OffAir);

        Assert.True(state.IsOffAir(7));
        Assert.False(state.IsOffAir(8));

        // A playlist row shows the video it got to, which may not be the one being sent.
        Assert.True(state.IsOffAir(1, 100));
        Assert.False(state.IsOffAir(1, 200));
    }

    [Fact]
    public void TheVideoOnAirIsRememberedAndFollowsABroadcastYouTubeReplaced()
    {
        var state = new YouTubeAirState();
        Assert.Null(state.OnAirVideoOf(7));

        state.Observe(7, OnAir("firstLive01"));
        Assert.Equal("firstLive01", state.OnAirVideoOf(7));

        // Off air does not forget it: it is the video asked about next.
        state.Observe(7, OffAir);
        Assert.Equal("firstLive01", state.OnAirVideoOf(7));

        // Auto-start opened a new broadcast for the same stream: that is the live now.
        state.Observe(7, OnAir("secondLive2"));
        Assert.Equal("secondLive2", state.OnAirVideoOf(7));
    }

    [Fact]
    public void ALiveIsStoppedOnlyAfterEnoughAnswersInARowSayTheBroadcastEnded()
    {
        var state = new YouTubeAirState();
        state.Observe(7, OnAir());

        Assert.False(state.ObserveEnding(7, true));
        Assert.True(state.ObserveEnding(7, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void AnAnswerThatIsNotAnEndStartsTheCountAgain(bool? answer)
    {
        var state = new YouTubeAirState();
        state.Observe(7, OnAir());

        state.ObserveEnding(7, true);
        Assert.False(state.ObserveEnding(7, answer));
        Assert.False(state.ObserveEnding(7, true));
    }

    [Fact]
    public void ALiveFoundAgainForgetsTheEndingsCounted()
    {
        var state = new YouTubeAirState();
        state.Observe(7, OnAir());
        state.ObserveEnding(7, true);

        state.Observe(7, OnAir());

        Assert.False(state.ObserveEnding(7, true));
    }

    [Theory]
    [InlineData("{\"isLiveContent\":true,\"isLive\":true}", false)]
    [InlineData("{\"isLiveContent\":true,\"isLiveNow\":true}", false)]
    [InlineData("{\"isLiveContent\":true,\"lengthSeconds\":\"3600\"}", true)]
    // A page this application does not understand is not an end: a change of the page of YouTube
    // must never stop a live that is on air.
    [InlineData("<html>consent</html>", null)]
    [InlineData(null, null)]
    public void ThePageOfAVideoSaysEndedOnlyWhenItIsALiveThatIsNoLongerLive(string? page, bool? ended)
    {
        Assert.Equal(ended, LivePlatformEmbeds.HasEnded(page));
    }
}
