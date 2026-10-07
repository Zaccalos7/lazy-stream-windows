using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Lookup = Orbis.Stream.Core.Streaming.LivePlatformEmbeds.LivePlatformLookup;

namespace Orbis.Stream.Tests;

public sealed class YouTubeAirWatchTests
{
    private const long Live = 7;

    private static Lookup OnAir(string videoId = "abcdefghijk") =>
        Lookup.Of(new LivePlatformEmbed("youtube", "channel", $"https://www.youtube.com/embed/{videoId}", videoId));

    private static readonly Lookup OffAir = Lookup.Nothing;
    private static readonly Lookup Unreachable = Lookup.Failed("YouTube did not return the streams page of @channel");

    [Fact]
    public void ALiveIsFlaggedOnlyAfterEnoughAnswersInARowSayItIsNotOnAir()
    {
        var state = new YouTubeAirState();

        // One answer is a moment: the page of the streams can lag the broadcast by a few seconds.
        Assert.False(state.Observe(Live, OffAir));
        Assert.False(state.IsOffAir(Live));

        Assert.True(state.Observe(Live, OffAir));
        Assert.True(state.IsOffAir(Live));

        // Flagged once: the next miss changes nothing the pages have to redraw.
        Assert.False(state.Observe(Live, OffAir));
    }

    [Fact]
    public void ALiveFoundOnAirClearsTheFlagAndStartsCountingAgain()
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OffAir);
        state.Observe(Live, OffAir);

        Assert.True(state.Observe(Live, OnAir()));
        Assert.False(state.IsOffAir(Live));

        // The misses before it are gone: one more is not enough to flag it again.
        state.Observe(Live, OffAir);
        Assert.False(state.IsOffAir(Live));
    }

    [Fact]
    public void APlatformThatCouldNotBeAskedLeavesTheMissesAsTheyWere()
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OffAir);
        state.Observe(Live, OffAir);

        // Not reachable is not off air, nor on air: the flag stays until the live is really found.
        state.Observe(Live, Unreachable);
        Assert.True(state.IsOffAir(Live));
    }

    [Fact]
    public void AChannelYouTubeKeepsFailingToAnswerForIsSaidToBeUnverified()
    {
        var state = new YouTubeAirState();

        Assert.False(state.Observe(Live, Unreachable));
        Assert.True(state.Observe(Live, Unreachable));
        Assert.True(state.IsUnverified(Live));
        Assert.False(state.IsOffAir(Live));

        // Any real answer, on air or off, means the channel can be checked after all.
        state.Observe(Live, OffAir);
        Assert.False(state.IsUnverified(Live));

        state.Observe(8, Unreachable);
        state.Observe(8, Unreachable);
        state.Observe(8, OnAir());
        Assert.False(state.IsUnverified(8));
    }

    [Fact]
    public void TheWarningStaysUntilTheUserClosesItAndComesBackOnlyAfterTheLiveWasFound()
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OffAir);
        state.Observe(Live, OffAir);

        Assert.True(state.Dismiss(Live));
        Assert.False(state.IsOffAir(Live));

        // Closed is closed while nothing changes.
        state.Observe(Live, OffAir);
        Assert.False(state.IsOffAir(Live));

        // Found on air, then gone again: that is a new problem, and it is shown.
        state.Observe(Live, OnAir());
        state.Observe(Live, OffAir);
        state.Observe(Live, OffAir);
        Assert.True(state.IsOffAir(Live));
    }

    [Fact]
    public void TheWaitRunsOnceForTheWholeLiveNotForEveryVideoOfIt()
    {
        var state = new YouTubeAirState();
        var start = new DateTimeOffset(2026, 10, 7, 21, 0, 0, TimeSpan.Zero);

        Assert.Equal(start, state.OnAirSince(Live, start));
        Assert.Equal(start, state.OnAirSince(Live, start.AddMinutes(3)));
    }

    [Fact]
    public void ALiveIsForgottenOnlyAfterSomePassesWithNothingRunning()
    {
        var state = new YouTubeAirState();
        var start = DateTimeOffset.UtcNow;
        state.OnAirSince(Live, start);
        state.Observe(Live, OffAir);
        state.Observe(Live, OffAir);

        // The moment between two videos of a playlist: the warning stays.
        Assert.False(state.Missing(Live));
        Assert.True(state.IsOffAir(Live));

        // Back again: the absence is forgiven.
        state.OnAirSince(Live, start.AddSeconds(30));
        Assert.False(state.Missing(Live));

        // Gone for good: forgotten, warning and all.
        Assert.True(state.Missing(Live));
        Assert.False(state.IsOffAir(Live));
    }

    [Fact]
    public void TheVideoOnAirIsRememberedAndFollowsABroadcastYouTubeReplaced()
    {
        var state = new YouTubeAirState();
        Assert.Null(state.OnAirVideoOf(Live));

        state.Observe(Live, OnAir("firstLive01"));
        Assert.Equal("firstLive01", state.OnAirVideoOf(Live));

        // Off air does not forget it: it is the video asked about next.
        state.Observe(Live, OffAir);
        Assert.Equal("firstLive01", state.OnAirVideoOf(Live));

        // Auto-start opened a new broadcast for the same stream: that is the live now.
        state.Observe(Live, OnAir("secondLive2"));
        Assert.Equal("secondLive2", state.OnAirVideoOf(Live));
    }

    [Fact]
    public void ALiveIsStoppedOnlyAfterEnoughAnswersInARowSayTheBroadcastEnded()
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OnAir());

        Assert.False(state.ObserveEnding(Live, true));
        Assert.True(state.ObserveEnding(Live, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void AnAnswerThatIsNotAnEndStartsTheCountAgain(bool? answer)
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OnAir());

        state.ObserveEnding(Live, true);
        Assert.False(state.ObserveEnding(Live, answer));
        Assert.False(state.ObserveEnding(Live, true));
    }

    [Fact]
    public void ALiveFoundAgainForgetsTheEndingsCounted()
    {
        var state = new YouTubeAirState();
        state.Observe(Live, OnAir());
        state.ObserveEnding(Live, true);

        state.Observe(Live, OnAir());

        Assert.False(state.ObserveEnding(Live, true));
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
