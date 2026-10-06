using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task YouTube_AStreamsPageThatArrivesEmpty_IsAChannelWithoutALive_NotAFailure()
    {
        // The page was read and had no video on it. That is the answer, and it is not the same as
        // never having got the page.
        var embeds = Platform(_ => Text(string.Empty));

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
}