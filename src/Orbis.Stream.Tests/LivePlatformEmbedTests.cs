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
    private const string YouTube = "rtmps://a.rtmp.youtube.com/live2";

    [Fact]
    public async Task Twitch_IsPlayedOnTheChannelOfTheConfiguration()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var embed = await embeds.ResolveAsync(Twitch, "reproChannel", "twitch", "localhost", default);

        Assert.NotNull(embed);
        Assert.Equal("twitch", embed!.Platform);
        Assert.Contains("channel=reprochannel", embed.Url, StringComparison.Ordinal);
        Assert.Contains("parent=localhost", embed.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twitch_AsksForTheNameOfThePageItIsPlayedOn()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var embed = await embeds.ResolveAsync(Twitch, "reproChannel", "twitch", "orbis.example", default);

        Assert.NotNull(embed);
        Assert.Contains("parent=orbis.example", embed!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIngestThisApplicationDoesNotKnow_HasNoPlayer()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(await embeds.ResolveAsync("rtmp://my.cdn.example/live", "channel", null, "localhost", default));
        Assert.Null(await embeds.ResolveAsync(null, "channel", null, "localhost", default));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(null, "")]
    public async Task ALiveWithoutAChannel_HasNoPlayer(string? channel, string? platformStreamName)
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(await embeds.ResolveAsync(Twitch, channel, platformStreamName, "localhost", default));
    }

    [Fact]
    public async Task ALiveThatNamesTheChannelElsewhere_IsPlayedOnIt()
    {
        // A live that came through the API keeps the channel in the field named platform stream name.
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var embed = await embeds.ResolveAsync(Twitch, null, "reproChannel", "localhost", default);

        Assert.NotNull(embed);
        Assert.Contains("channel=reprochannel", embed!.Url, StringComparison.Ordinal);
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

        var embed = await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default);

        Assert.NotNull(embed);
        Assert.Equal("youtube", embed!.Platform);
        Assert.Equal("reproChannel", embed.Channel);
        Assert.StartsWith("https://www.youtube.com/embed/RU6gEobXVHA?", embed.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_TrimsTheAtSignOfTheHandle()
    {
        var handler = Page(_ => Watch(onAir: true));
        var embeds = new LivePlatformEmbeds(new HttpClient(handler), NullLogger<LivePlatformEmbeds>.Instance);

        var embed = await embeds.ResolveAsync(YouTube, "@reproChannel", "youtube", "localhost", default);

        Assert.NotNull(embed);
        Assert.StartsWith("https://www.youtube.com/@reproChannel/streams", handler.Asked[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_AChannelThatIsNotOnAir_HasNoPlayer()
    {
        // The top of the page of the streams of a channel that is off air is its last broadcast: a
        // player given that id plays an old video, which is a worse answer than none.
        var embeds = Platform(request => Text(
            request.RequestUri!.AbsolutePath.EndsWith("/streams", StringComparison.Ordinal)
                ? Streams("RU6gEobXVHA")
                : Watch(onAir: false)));

        Assert.Null(await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default));
    }

    [Fact]
    public async Task YouTube_ThatCannotBeAsked_HasNoPlayer()
    {
        var embeds = Platform(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });

        Assert.Null(await embeds.ResolveAsync(YouTube, "reproChannel", "youtube", "localhost", default));
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

    /// <summary>A page of a video, which says whether it is live content.</summary>
    private static string Watch(bool onAir) =>
        "<html><script>var ytInitialPlayerResponse = " +
        "{\"isLiveContent\":" + (onAir ? "true" : "false") + "}};</script></html>";

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