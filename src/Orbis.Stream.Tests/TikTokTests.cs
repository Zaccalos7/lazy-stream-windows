using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// TikTok LIVE goes out the way YouTube does - the ffmpeg sender keeps the time with a head start -
/// and standing up: the frame of the live is 9:16 whatever the first file was, and a landscape
/// source is fitted into it.
/// </summary>
public sealed class TikTokDeliveryTests
{
    private const string Ingest = "rtmp://push-rtmp-l11-va01.tiktokcdn.com/stage/stream-123?expire=1&sign=abc";

    private static readonly MediaOutput Upright = new(1080, 1920, 30d);

    private static readonly MediaOutput FullHd = new(1920, 1080, 30d);

    private static VideoSettingEntity Setting(int? width = null, int? height = null) => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 4_500_000,
        VideoFormat = "flv",
        GopSize = 2,
        VideoWidth = width,
        VideoHeight = height,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    [Fact]
    public void TikTokIsDeliveredTheWayYouTubeIsStandingUp()
    {
        var tiktok = StreamPlatformProfile.TikTok;

        Assert.Same(tiktok, StreamPlatformProfile.For(Ingest));
        Assert.Equal(StreamPlatform.TikTok, tiktok.Platform);
        Assert.Equal(RelayPacing.Sender, tiktok.Pacing);
        Assert.Equal(RelayTransport.FfmpegSender, tiktok.Transport);
        Assert.True(tiktok.Preroll > TimeSpan.Zero);
        Assert.Same(StreamPlatformProfile.YouTube.Adaptation, tiktok.Adaptation);
        Assert.True(tiktok.RequiresAudio);
        Assert.Equal(48_000, tiktok.AudioSampleRate);
        Assert.True(tiktok.UniformFormat);
        Assert.True(tiktok.Portrait);
        Assert.False(tiktok.ChoosableTransport);
        Assert.Equal(TimeSpan.FromSeconds(1), tiktok.RateBuffer);
        Assert.Equal(2, tiktok.KeyframeSeconds);

        // Every other platform lies down.
        foreach (var other in new[] { StreamPlatformProfile.Twitch, StreamPlatformProfile.YouTube, StreamPlatformProfile.Kick, StreamPlatformProfile.Facebook, StreamPlatformProfile.Generic })
        {
            Assert.False(other.Portrait);
        }
    }

    [Fact]
    public void TikTokStandsThePictureUp()
    {
        Assert.Equal(Upright, StreamPlatformProfile.TikTok.Orient(FullHd));
        Assert.Equal(Upright, StreamPlatformProfile.TikTok.Orient(Upright));
        Assert.Equal(FullHd, StreamPlatformProfile.Twitch.Orient(FullHd));
        Assert.Equal(Upright, StreamPlatformProfile.YouTube.Orient(Upright));
    }

    [Theory]
    // A landscape file, named or not, stands the live up at the same size turned.
    [InlineData(1920, 1080, null, null, 1080, 1920)]
    [InlineData(1280, 720, 1280, 720, 720, 1280)]
    // A portrait setting is already upright; a short in portrait starts the live at 1080x1920.
    [InlineData(1920, 1080, 1080, 1920, 1080, 1920)]
    [InlineData(432, 768, null, null, 1080, 1920)]
    public void TheFirstVideoOfATikTokLiveFixesAPictureThatStandsUp(
        int width, int height, int? settingWidth, int? settingHeight, int liveWidth, int liveHeight)
    {
        var setting = Setting(settingWidth, settingHeight);
        var own = FfmpegCommandBuilder.ResolveOutput(setting, new MediaProbeResult(width, height, 30d, true, 2, 60d));

        var frame = StreamPlatformProfile.TikTok.Orient(FfmpegCommandBuilder.FrameOfLive(setting, own));

        Assert.Equal(new MediaOutput(liveWidth, liveHeight, 30d), frame);
    }

    [Fact]
    public void ALandscapeFileOnTikTokIsFittedIntoTheUprightFrameAndPreviewedUpright()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", Ingest, new MediaProbeResult(1920, 1080, 30d, true, 2, 60d), Setting(),
            PreviewPath: "/tmp/preview.jpg", Profile: StreamPlatformProfile.TikTok, Frame: Upright)));

        Assert.Contains(
            "-vf scale=1080:1920:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1",
            command,
            StringComparison.Ordinal);
        // The preview is bounded on its height: 360x640, the pixels of the landscape one turned.
        Assert.Contains("setsar=1,fps=30,scale=w=-2:h='min(640,ih)'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ALandscapeCanvasOnTikTokIsFittedIntoTheUprightFrame()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1920, 1080, false)],
            Ingest,
            Setting(),
            1920,
            1080,
            30d,
            PreviewPath: "/tmp/preview.jpg",
            Profile: StreamPlatformProfile.TikTok,
            Frame: StreamPlatformProfile.TikTok.Orient(FullHd))));

        Assert.Contains("[orbisv]scale=1080:1920:force_original_aspect_ratio=decrease", command, StringComparison.Ordinal);
        Assert.Contains("[orbisp0]fps=30,scale=w=-2:h='min(640,ih)'[orbisp]", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUprightCanvasGoesOutAsItIs()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1080, 1920, false)],
            Ingest,
            Setting(),
            1080,
            1920,
            30d,
            Profile: StreamPlatformProfile.TikTok,
            Frame: StreamPlatformProfile.TikTok.Orient(Upright))));

        Assert.Contains("color=c=black:s=1080x1920", command, StringComparison.Ordinal);
        Assert.DoesNotContain("orbisvfit", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TikTokGoesOutThroughAnFfmpegSenderThatReadsAtRealTimeWithItsBurst()
    {
        var output = LiveOutput.For(Ingest, new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger.Instance, RelayTransport.NativeRtmp);

        // The channel asks for nothing on TikTok: it goes out the way its kind does.
        Assert.Equal(StreamPlatformProfile.TikTok, output!.Profile);
        var arguments = string.Join(' ', FfmpegCommandBuilder.BuildSender(Ingest, output.Profile));
        Assert.Contains("-readrate 1 -readrate_initial_burst 2 -f flv -i pipe:0", arguments, StringComparison.Ordinal);
        Assert.EndsWith(Ingest, arguments, StringComparison.Ordinal);
    }
}

/// <summary>
/// TikTok hands out a stream key for every live, and it expires: a configuration of TikTok keeps
/// none, the key is typed in when a live starts - from the wizard, the playlist or the row of a
/// live started again - and a live never goes out with the stand-in the configuration keeps.
/// </summary>
public sealed class TikTokKeyTests : IClassFixture<ApplicationFixture>
{
    private const string Server = "rtmp://push-rtmp-l11-va01.tiktokcdn.com/stage";

    private readonly ApplicationFixture _fixture;

    public TikTokKeyTests(ApplicationFixture fixture) => _fixture = fixture;

    private T Service<T>() where T : notnull => _fixture.Services.GetRequiredService<T>();

    private static SettingRequest Configuration(string platform, string channel, string? key, string url = Server) =>
        new(url, key, platform, string.Empty, null, true, channel, false, 0, 0);

    [Fact]
    public void A_configuration_of_TikTok_keeps_no_key()
    {
        var validator = Service<RequestValidator>();
        var settings = Service<SettingService>();
        var repository = Service<SettingRepository>();

        // No key to give: the configuration is valid without one, and keeps the stand-in of its channel.
        validator.RequireSetting(Configuration("tiktok", "first.account", null));
        settings.AddNewConfiguration(Configuration("tiktok", "first.account", null));
        var first = Assert.Single(repository.FindAll(new Dictionary<string, string> { ["channelName"] = "first.account" }));
        Assert.Equal(LiveStreamKeys.StandInFor("first.account"), first.StreamKey);

        // A key typed in anyway is not kept: it would be stale by the next live.
        settings.AddNewConfiguration(Configuration("tiktok", "second.account", "stream-123?expire=1"));
        var second = Assert.Single(repository.FindAll(new Dictionary<string, string> { ["channelName"] = "second.account" }));
        Assert.True(LiveStreamKeys.IsStandIn(second.StreamKey));

        // Two accounts on the same server are two configurations; the same account twice is one.
        Assert.Throws<DuplicationEntityException>(() => settings.AddNewConfiguration(Configuration("tiktok", "first.account", null)));

        // Any other platform still needs its key.
        Assert.Throws<RequestValidationException>(() => validator.RequireSetting(Configuration("twitch", "a.twitch.channel", null, "rtmp://live.twitch.tv/app")));

        // A configuration turned into a TikTok one forgets the key it had.
        settings.AddNewConfiguration(Configuration("twitch", "turned.channel", "live_123_secret", "rtmp://live.twitch.tv/app"));
        var turned = Assert.Single(repository.FindAll(new Dictionary<string, string> { ["channelName"] = "turned.channel" }));
        settings.ModifySetting(turned.Id, Configuration("tiktok", "turned.channel", null) with { StreamKey = null });
        Assert.Equal(LiveStreamKeys.StandInFor("turned.channel"), repository.FindById(turned.Id)!.StreamKey);
    }

    [Fact]
    public void The_key_of_a_live_to_TikTok_is_the_one_typed_for_it()
    {
        var tiktok = new SettingResponse(1, Server, LiveStreamKeys.StandInFor("me"), "tiktok", null, null, null, null, true, "me", false, 0, 0);
        var twitch = new SettingResponse(2, "rtmp://live.twitch.tv/app", "live_123", "twitch", null, null, null, null, true, "me", false, 0, 0);

        Assert.Equal("stream-456?expire=2", MainLiveModel.KeyFor(tiktok, "  stream-456?expire=2 "));
        Assert.Null(MainLiveModel.KeyFor(tiktok, "   "));
        Assert.Null(MainLiveModel.KeyFor(tiktok, null));
        // A configuration of any other platform goes out with the key it keeps, whatever is typed.
        Assert.Equal("live_123", MainLiveModel.KeyFor(twitch, "ignored"));
    }

    [Fact]
    public void A_live_never_goes_out_with_the_stand_in()
    {
        var streaming = Service<StreamingService>();
        var refused = Assert.Throws<LiveException>(() => streaming.StartLive(new StartLiveRequest(
            Server, LiveStreamKeys.StandInFor("me"), "/videos/nowhere.mp4", "tiktok", "stand-in.channel", null)));

        Assert.Equal("stream.key.required", refused.MessageCode);
    }

    [Fact]
    public void A_live_to_TikTok_started_again_goes_out_with_the_key_of_the_new_live()
    {
        // The live really starts: its ingest is one nobody listens on, so the test never reaches
        // TikTok. The platform is the name the live was started with, as for a live of the page.
        var histories = Service<VideoLiveHistoryRepository>();
        var historyPkid = histories.Insert(new VideoLiveHistoryEntity
        {
            FolderOfVideoToStream = "/videos/yesterday-tiktok.mp4",
            LocalDateTimeStartLive = DateTime.Now.AddDays(-1),
            StreamUrl = "rtmp://127.0.0.1:9/stage",
            StreamKey = LiveStreamKeys.StandInFor("restarted.account"),
            PlatformStreamName = "tiktok",
            UserName = StreamingService.CurrentUserName
        });
        var pkid = Service<VideoRepository>().Insert(new VideoEntity
        {
            Name = "yesterday-tiktok.mp4",
            VideoPath = "/videos/yesterday-tiktok.mp4",
            Extension = "mp4",
            LiveStatus = LiveStatus.Ended,
            VideoLiveHistoryId = historyPkid,
            ChannelName = "restarted.account"
        });

        var streaming = Service<StreamingService>();
        var video = Service<VideoService>().FindVideo(pkid);

        // Without a key of its own it is refused, instead of going out with the stand-in.
        Assert.Equal("stream.key.required", Assert.Throws<LiveException>(() => streaming.StartVideo(video)).MessageCode);

        // With the key of the new live it goes: the history of that start keeps the key it went out with.
        var started = streaming.StartVideo(video, "  stream-789?expire=3 ");
        Assert.Equal((int)HttpStatusCode.Accepted, started.StatusCode);

        using var connection = Service<SqliteConnectionFactory>().Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stream_key FROM video_live_history WHERE platform_stream_name = 'tiktok' ORDER BY pkid DESC LIMIT 1;";
        Assert.Equal("stream-789?expire=3", command.ExecuteScalar());
    }

    [Fact]
    public async Task The_channel_page_saves_a_TikTok_configuration_without_a_key_and_never_shows_the_stand_in()
    {
        // The token goes with the cookie of the client that drew the page: each client keeps its own.
        using var page = await _fixture.NoRedirectClient.GetAsync("/orbis/mainChannelSetting?edit=new");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"");
        Assert.True(token.Success, "The channel page draws no antiforgery token.");
        // The key field hides for TikTok, where a hint of its own says why.
        Assert.Contains("data-hide-when=\"tiktok\"", html, StringComparison.Ordinal);
        Assert.Contains("data-show-when=\"tiktok\"", html, StringComparison.Ordinal);

        using var saved = await _fixture.NoRedirectClient.PostAsync("/orbis/mainChannelSetting?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token.Groups["token"].Value,
            ["platformStreamName"] = "tiktok",
            ["channelName"] = "page.account",
            ["streamUrl"] = Server,
            ["description"] = string.Empty,
            ["autoCleanupEnabled"] = "false",
            ["autoCleanupIntervalMonths"] = "0",
            ["autoCleanupOlderThanMonths"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var configuration = Assert.Single(Service<SettingRepository>().FindAll(new Dictionary<string, string> { ["channelName"] = "page.account" }));
        Assert.True(LiveStreamKeys.IsStandIn(configuration.StreamKey));

        using var edit = await _fixture.Client.GetAsync($"/orbis/mainChannelSetting?edit={configuration.Id}");
        Assert.DoesNotContain(LiveStreamKeys.StandInPrefix, await edit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wizard_asks_for_the_key_of_a_live_to_TikTok_only()
    {
        Service<SettingService>().AddNewConfiguration(Configuration("tiktok", "wizard.account", null));
        Service<SettingService>().AddNewConfiguration(Configuration("twitch", "wizard.twitch", "live_999", "rtmp://live.twitch.tv/app"));

        var html = await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1");

        // The pick of a TikTok configuration says so, and stands the canvas up; the key goes with
        // the start from the step to the canvas, and is never written into the page.
        Assert.Matches("value=\"\\d+\" required\\s+data-key-each-live=\"1\" data-portrait=\"1\" />", html);
        // A Twitch configuration says neither: its key is the one it keeps, and its live lies down.
        Assert.Matches("value=\"\\d+\" required\\s+/>", html);
        Assert.Contains("data-live-key", html, StringComparison.Ordinal);
        Assert.Contains("data-wizard-fields=\"settingId configurationId streamKey\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveStreamKeys.StandInPrefix, html, StringComparison.Ordinal);
        Assert.DoesNotContain("live_999", html, StringComparison.Ordinal);
    }
}
