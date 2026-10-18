using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// Where a live can be watched with the player of its platform. The whole point of these is the
/// channel: a live started from the wizard keeps the choice of platform in the field named platform
/// stream name, so a player addressed by that field plays the channel called "twitch".
/// </summary>
public sealed class LivePlatformEmbedTests
{
    private const string Twitch = "rtmp://live.twitch.tv/app";
    private const string YouTube = "rtmps://a.rtmps.youtube.com/live2";
    private const string Kick = "rtmps://fa723fc1b171.global-contribute.live-video.net/app";
    private const string Facebook = "rtmps://live-api-s.facebook.com:443/rtmp/";

    [Theory]
    [InlineData("ReproChannel")]
    [InlineData("https://kick.com/ReproChannel/")]
    public async Task Kick_PlaysTheConfiguredChannelWithoutDownloadingPlatformPages(string channel)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No lookup needed"));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);
        var found = await embeds.ResolveAsync(Kick, channel, "kick", "localhost", default);
        Assert.NotNull(found.Embed);
        Assert.Equal("kick", found.Embed.Platform);
        Assert.Equal("https://player.kick.com/reprochannel?autoplay=true&muted=true", found.Embed.Url);
        Assert.Equal("https://kick.com/reprochannel", found.Embed.WatchUrl);
        Assert.Empty(handler.Asked);
    }

    [Theory]
    [InlineData("https://www.facebook.com/my.page/videos/123456789/")]
    [InlineData("https://m.facebook.com/watch/?v=123456789&tracking=ignored")]
    [InlineData("https://www.facebook.com/video.php?v=123456789")]
    [InlineData("https://www.facebook.com/watch/live/?v=123456789&ref=watch_permalink")]
    [InlineData("https://www.facebook.com/videos/title/123456789/")]
    public async Task Facebook_EmbedsTheSpecifiedPublicVideo(string watchUrl)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No lookup needed"));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);
        var found = await embeds.ResolveAsync(Facebook, "My Page", "facebook", "localhost", default, watchUrl);
        Assert.NotNull(found.Embed);
        Assert.Equal("facebook", found.Embed.Platform);
        Assert.Equal("https://www.facebook.com/watch/?v=123456789", found.Embed.WatchUrl);
        Assert.StartsWith("https://www.facebook.com/plugins/video.php?href=", found.Embed.Url);
        Assert.Contains(Uri.EscapeDataString(found.Embed.WatchUrl!), found.Embed.Url);
        Assert.DoesNotContain("tracking", found.Embed.Url);
        Assert.Empty(handler.Asked);
    }

    [Fact]
    public async Task Facebook_CanUseAPublicVideoUrlAlreadyStoredAsTheChannel()
    {
        var embeds = Platform(_ => throw new InvalidOperationException());
        var found = await embeds.ResolveAsync(Facebook, "https://www.facebook.com/page/videos/123/", "facebook", null, default);
        Assert.Equal("https://www.facebook.com/watch/?v=123", found.Embed?.WatchUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("My Page")]
    [InlineData("https://www.facebook.com/page/live")]
    [InlineData("https://www.facebook.com/live/producer/v2/")]
    [InlineData("https://www.facebook.com.evil.example/videos/123/")]
    [InlineData("https://www.facebook.com@evil.example/videos/123/")]
    [InlineData("http://localhost/videos/123/")]
    [InlineData("javascript:alert(1)")]
    public async Task Facebook_DoesNotGuessAVideoOrLoadAnUnrelatedUrl(string? watchUrl)
    {
        var embeds = Platform(_ => throw new InvalidOperationException());
        var found = await embeds.ResolveAsync(Facebook, "Page", "facebook", null, default, watchUrl);
        Assert.Null(found.Embed);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public async Task Twitch_IsPlayedOnTheChannelOfTheConfiguration()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var found = await embeds.ResolveAsync(Twitch, "reproChannel", "twitch", "localhost", default);

        Assert.NotNull(found.Embed);
        Assert.Equal("twitch", found.Embed!.Platform);
        Assert.Contains("channel=reprochannel", found.Embed.Url, StringComparison.Ordinal);
        Assert.Contains("parent=localhost", found.Embed.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twitch_AsksForTheNameOfThePageItIsPlayedOn()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var found = await embeds.ResolveAsync(Twitch, "reproChannel", "twitch", "orbis.example", default);

        Assert.NotNull(found.Embed);
        Assert.Contains("parent=orbis.example", found.Embed!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIngestThisApplicationDoesNotKnow_HasNoPlayer()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null((await embeds.ResolveAsync("rtmp://my.cdn.example/live", "channel", null, "localhost", default)).Embed);
        Assert.Null((await embeds.ResolveAsync(null, "channel", null, "localhost", default)).Embed);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(null, "")]
    public async Task ALiveWithoutAChannel_HasNoPlayer(string? channel, string? platformStreamName)
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null((await embeds.ResolveAsync(Twitch, channel, platformStreamName, "localhost", default)).Embed);
    }

    [Fact]
    public async Task ALiveThatNamesTheChannelElsewhere_IsPlayedOnIt()
    {
        // A live that came through the API keeps the channel in the field named platform stream name.
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var found = await embeds.ResolveAsync(Twitch, null, "reproChannel", "localhost", default);

        Assert.NotNull(found.Embed);
        Assert.Contains("channel=reprochannel", found.Embed!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_IsPlayedOnTheVideoTheChannelIsBroadcasting()
    {
        var embeds = Platform(request =>
        {
            var page = request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
                ? Streams("RU6gEobXVHA")
                : Watch(onAir: true);
            return Text(page);
        });

        var found = await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        Assert.NotNull(found.Embed);
        Assert.Equal("youtube", found.Embed!.Platform);
        Assert.Equal("reproChannel", found.Embed.Channel);
        Assert.StartsWith("https://www.youtube.com/embed/RU6gEobXVHA?", found.Embed.Url, StringComparison.Ordinal);
        Assert.Contains("enablejsapi=1", found.Embed.Url, StringComparison.Ordinal);
        Assert.Contains("mute=1", found.Embed.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_ABroadcastThatHasEnded_HasNoPlayer()
    {
        // An ended live is still live content: only the fields of the broadcast say it is over.
        var embeds = Platform(request => Text(
            request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
                ? Streams("RU6gEobXVHA")
                : Ended()));

        Assert.Null((await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default)).Embed);
    }

    [Fact]
    public async Task YouTube_TrimsTheAtSignOfTheHandle()
    {
        var handler = Page(_ => Watch(onAir: true));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);

        var found = await embeds.ResolveAsync(YouTube, "@reproChannel", "youtube", "localhost", default);

        Assert.NotNull(found.Embed);
        Assert.StartsWith("https://www.youtube.com/@reproChannel/streams", handler.Asked[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_AsksTheStreamsPageOfAChannelIdOnThePageOfAChannelId()
    {
        // A configuration is filled in with whatever the studio hands over, and the two are not the
        // same address: /@handle is the handle and /channel/UC... is the id. The preview asks out of
        // the same builder the live page links with, so the two cannot disagree about the channel.
        var handler = Page(_ => Watch(onAir: true));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);

        var found = await embeds.ResolveAsync(YouTube, "UCuAXFkgsw1L7xaCfnd5JJOw", "youtube", "localhost", default);

        Assert.NotNull(found.Embed);
        Assert.StartsWith(
            "https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw/streams",
            handler.Asked[0].ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_AChannelThatIsNotOnAir_HasNoPlayer_AndNoReasonToBlameThePlatform()
    {
        // The top of the page of the streams of a channel that is off air is its last broadcast: a
        // player given that id plays an old video, which is a worse answer than none.
        var embeds = Platform(request => Text(
            request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
                ? Streams("RU6gEobXVHA")
                : Watch(onAir: false)));

        var found = await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        Assert.Null(found.Embed);

        // Nothing is wrong and nothing is to be said: this is an answer, not a failure.
        Assert.Null(found.Reason);
    }

    [Fact]
    public async Task YouTube_APageThatNeverArrives_SaysSo_InsteadOfSayingTheChannelIsOffAir()
    {
        // The whole reason the reason exists. A handle that is not a handle, a platform that answers
        // this machine with a page that is not YouTube and a network that is down all end here, and
        // a page told "the channel is not live" would say it for ever about a live that is fine.
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var found = await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        Assert.Null(found.Embed);
        Assert.NotNull(found.Reason);
        Assert.Contains("reproChannel", found.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_ARecognizedEmptyGrid_IsAChannelWithoutALive_NotAFailure()
    {
        // The page was read and had no video on it. That is the answer, and it is not the same as
        // never having got the page.
        var embeds = Platform(_ => Text("{\"contents\":[]}"));

        var found = await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        Assert.Null(found.Embed);
        Assert.Null(found.Reason);
    }

    [Fact]
    public async Task YouTube_IsAskedOnce_WhileTheAnswerIsStillGood()
    {
        var handler = Page(_ => Watch(onAir: true));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);

        await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);
        await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        // One page of streams and one page of the video, however many pages ask.
        Assert.Equal(2, handler.Asked.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task YouTube_AnOfflineOrFailedLookupIsSharedUntilTheShortCacheExpires(bool failure)
    {
        var clock = new LookupClock();
        var handler = new RecordingHandler(_ => failure
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Text("{\"contents\":[]}"));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance, clock);

        var first = await embeds.ResolveAsync(YouTube, "channel", null, "localhost", default);
        var second = await embeds.ResolveAsync(YouTube, "channel", null, "localhost", default);
        Assert.Equal(first, second);
        Assert.Equal(failure ? 3 : 1, handler.Asked.Count);
        Assert.Equal(failure, second.Reason is not null);

        clock.Advance(TimeSpan.FromSeconds(30));
        await embeds.ResolveAsync(YouTube, "channel", null, "localhost", default);
        Assert.Equal(failure ? 6 : 2, handler.Asked.Count);
    }

    [Fact]
    public async Task YouTube_RechecksAfterTheWarningAndClearsItWhenThePublicBroadcastStarts()
    {
        var clock = new LookupClock();
        var online = false;
        var handler = Page(_ => Watch(online));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance, clock);
        var state = new YouTubeAirState();
        const long history = 7;

        for (var pass = 0; pass < 2; pass++)
        {
            state.Observe(history, await embeds.ResolveAsync(YouTube, "channel", null, null, default));
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.True(state.IsOffAir(history));

        online = true;
        var found = await embeds.ResolveAsync(YouTube, "channel", null, null, default);
        Assert.True(state.Observe(history, found));
        Assert.False(state.IsOffAir(history));
        Assert.Equal("RU6gEobXVHA", state.OnAirVideoOf(history));
        Assert.Equal(6, handler.Asked.Count);
    }

    [Fact]
    public async Task YouTube_ExpiredSuccessIsNotReusedWhenTheNextLookupFails()
    {
        var clock = new LookupClock();
        var fail = false;
        var handler = Page(_ => fail ? throw new InvalidOperationException("lookup failed") : Watch(true));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance, clock);

        Assert.NotNull((await embeds.ResolveAsync(YouTube, "channel", null, null, default)).Embed);
        clock.Advance(TimeSpan.FromSeconds(60));
        fail = true;
        var found = await embeds.ResolveAsync(YouTube, "channel", null, null, default);
        Assert.Null(found.Embed);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public void CandidateOf_TakesTheTopOfTheGrid_AndNotTheVideoElsewhereOnThePage()
    {
        var page = """
            <script>var ytInitialData = {"a":"M3HKLzjvKPc","contents":[
            {"contentId":"RU6gEobXVHA","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"},
            {"contentId":"awQzjn72bI0","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"}]};</script>
            """;

        Assert.Equal("RU6gEobXVHA", LivePlatformEmbeds.CandidateOf(page));
        Assert.Null(LivePlatformEmbeds.CandidateOf("<html>nothing here</html>"));
    }

    [Fact]
    public async Task YouTube_FindsALiveAfterAnEndedVideoAndToleratesReorderedProperties()
    {
        var embeds = Platform(request => Text(request.RequestUri!.AbsolutePath.EndsWith("/streams")
            ? """
              <script>window["ytInitialData"] = {"contents":[
              {"contentType":"LOCKUP_CONTENT_TYPE_VIDEO","other":true,"contentId":"oldVideo001"},
              {"contentType":"LOCKUP_CONTENT_TYPE_VIDEO","contentId":"newVideo002"}]};</script>
              """
            : request.RequestUri.Query.Contains("oldVideo001") ? Ended() : Watch(true)));
        var found = await embeds.ResolveAsync(YouTube, "channel", null, null, default);
        Assert.Equal("newVideo002", found.Embed?.VideoId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>consent</html>")]
    [InlineData("<script>var ytInitialData = {\"responseContext\":{}};</script>")]
    public async Task YouTube_AnUnreadablePageIsUnverifiedRatherThanOffAir(string page)
    {
        var found = await Platform(_ => Text(page)).ResolveAsync(YouTube, "channel", null, null, default);
        Assert.Null(found.Embed);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public async Task YouTube_AConsentWatchPageDoesNotProveThatTheVideoIsOffAir()
    {
        var found = await Platform(request => Text(request.RequestUri!.AbsolutePath.EndsWith("/streams")
            ? Streams("RU6gEobXVHA") : "<html>consent</html>"))
            .ResolveAsync(YouTube, "channel", null, null, default);
        Assert.NotNull(found.Reason);
    }

    [Fact]
    public async Task YouTube_AManualRecheckDiscardsACachedNegativeAnswer()
    {
        var live = false;
        var embeds = Platform(request => Text(request.RequestUri!.AbsolutePath.EndsWith("/streams")
            ? Streams("RU6gEobXVHA") : Watch(live)));
        Assert.Null((await embeds.ResolveAsync(YouTube, "channel", null, null, default)).Embed);
        live = true;
        await embeds.RefreshYouTubeAsync("@channel", default);
        Assert.NotNull((await embeds.ResolveAsync(YouTube, "channel", null, null, default)).Embed);
    }

    [Fact]
    public void YouTube_IgnoresOtherTabsAndPrioritizesLiveBadges()
    {
        const string page = """
          {"tabs":[
          {"tabRenderer":{"selected":false,"content":{"contentId":"wrongVideo1","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"}}},
          {"tabRenderer":{"selected":true,"content":[
            {"contentId":"oldVideo001","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"},
            {"contentId":"newVideo002","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","badge":{"style":"LIVE"}}]}}]}
          """;
        Assert.Equal(new[] { "newVideo002", "oldVideo001" }, LivePlatformEmbeds.CandidatesOf(page));
    }

    [Fact]
    public void YouTube_ARecommendedLiveDoesNotTurnAnEndedBroadcastIntoALiveOne()
    {
        var page = Ended() + """<script>var ytInitialData = {"recommendation":{"isLiveNow":true}};</script>""";
        Assert.True(LivePlatformEmbeds.HasEnded(page));
    }

    [Fact]
    public void YouTube_ReadsTheAssignedJsonEvenWhenAnotherStatementFollows()
    {
        const string page = """<script>var ytInitialData={"contents":[{"videoId":"newVideo002","thumbnail":{}}]};window.extra=1;</script>""";
        Assert.Equal("newVideo002", LivePlatformEmbeds.CandidateOf(page));
    }

    [Fact]
    public async Task YouTube_AConsentPageCanBeRecoveredThroughTheDirectLiveAddress()
    {
        var handler = new RecordingHandler(request => Text(request.RequestUri!.AbsolutePath.EndsWith("/live")
            ? """{"playabilityStatus":{"status":"OK"},"videoDetails":{"videoId":"newVideo002","isLive":true,"isLiveContent":true}}"""
            : "<html>consent</html>"));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);
        var found = await embeds.ResolveAsync(YouTube, "@channel", "youtube", null, default);
        Assert.Equal("newVideo002", found.Embed?.VideoId);
        Assert.Equal(2, handler.Asked.Count);
        Assert.Contains("/live?ucbcb=1", handler.Asked[1].ToString());
    }

    [Fact]
    public async Task YouTube_CanRecoverTheGridWithoutMistakingAnArchivedDirectVideoForTheLive()
    {
        var embeds = Platform(request => Text(request.RequestUri!.AbsolutePath.EndsWith("/live") ? Ended()
            : request.RequestUri.AbsolutePath.EndsWith("/streams")
                ? request.RequestUri.Query.Length == 0 ? "<html>consent</html>" : Streams("newVideo002")
                : Watch(true)));
        var found = await embeds.ResolveAsync(YouTube, "channel", "youtube", null, default);
        Assert.Equal("newVideo002", found.Embed?.VideoId);
    }

    [Theory]
    [InlineData("LIVE_STREAM_OFFLINE")]
    [InlineData("UNPLAYABLE")]
    [InlineData("LOGIN_REQUIRED")]
    public void YouTube_AnUnplayablePlayerCannotConfirmTheStartEvenWithALiveFlag(string status)
    {
        var page = "{\"playabilityStatus\":{\"status\":\"" + status + "\"},\"videoDetails\":{\"isLive\":true,\"isLiveContent\":true}}";
        Assert.Null(LivePlatformEmbeds.HasEnded(page));
        Assert.Null(LivePlatformEmbeds.LiveVideoOf(page));
    }

    private static LivePlatformEmbeds Platform(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(new HttpClient(new RecordingHandler(answer)), NullLogger<LivePlatformEmbeds>.Instance);

    /// <summary>
    /// A browser that answers every page of a channel with the same grid and every page of a video
    /// with the same answer about it being live.
    /// </summary>
    private static RecordingHandler Page(Func<HttpRequestMessage, string> watch) => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
            ? Text(Streams("RU6gEobXVHA"))
            : Text(watch(request)));

    private static HttpResponseMessage Text(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    /// <summary>A page of the streams of a channel whose grid starts with the given video.</summary>
    private static string Streams(string videoId) =>
        "<html><script>var ytInitialData = " +
        "{\"lockupViewModel\":{\"contentId\":\"" + videoId + "\",\"contentType\":\"LOCKUP_CONTENT_TYPE_VIDEO\"}" +
        "};</script></html>";

    /// <summary>A page of a live video, which says whether it is on air.</summary>
    private static string Watch(bool onAir) =>
        "<html><script>var ytInitialPlayerResponse = " +
        "{\"videoDetails\":{\"isLiveContent\":true" + (onAir ? ",\"isLive\":true" : "") + "}," +
        "\"microformat\":{\"liveBroadcastDetails\":{\"isLiveNow\":" + (onAir ? "true" : "false") + "}}};</script></html>";

    /// <summary>The page of a broadcast that has ended, as YouTube serves it.</summary>
    private static string Ended() =>
        "<html><script>var ytInitialPlayerResponse = " +
        "{\"videoDetails\":{\"isLiveContent\":true}," +
        "\"microformat\":{\"liveBroadcastDetails\":{\"isLiveNow\":false," +
        "\"startTimestamp\":\"2026-10-02T07:29:10+00:00\",\"endTimestamp\":\"2026-10-02T12:26:31+00:00\"}}};</script></html>";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class LookupClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
