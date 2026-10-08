using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.SystemInfo;

namespace Orbis.Stream.Tests;

public sealed class LiveStatusTests
{
    [Fact]
    public void Ordinals_MatchTheHibernateColumnOfTheJavaEntity()
    {
        Assert.Equal(0, (int)LiveStatus.Live);
        Assert.Equal(1, (int)LiveStatus.Offline);
        Assert.Equal(2, (int)LiveStatus.Ended);
        Assert.Equal(3, (int)LiveStatus.Error);
        Assert.Equal(4, (int)LiveStatus.Stopped);
    }

    [Theory]
    [InlineData(0, LiveStatus.Live)]
    [InlineData(1, LiveStatus.Offline)]
    [InlineData(2, LiveStatus.Ended)]
    [InlineData(3, LiveStatus.Error)]
    [InlineData(4, LiveStatus.Stopped)]
    public void FromStorage_ReadsTheHibernateOrdinal(object value, LiveStatus expected)
    {
        Assert.Equal(expected, LiveStatusExtensions.FromStorage(value));
    }

    [Theory]
    [InlineData("LIVE", LiveStatus.Live)]
    [InlineData("stopped", LiveStatus.Stopped)]
    [InlineData("2", LiveStatus.Ended)]
    public void FromStorage_AlsoReadsTheWireNameAndTheOrdinalAsText(string value, LiveStatus expected)
    {
        Assert.Equal(expected, LiveStatusExtensions.FromStorage(value));
    }

    [Fact]
    public void FromStorage_FallsBackToOfflineForUnknownValues()
    {
        Assert.Equal(LiveStatus.Offline, LiveStatusExtensions.FromStorage(null));
        Assert.Equal(LiveStatus.Offline, LiveStatusExtensions.FromStorage("whatever"));
        Assert.Equal(LiveStatus.Offline, LiveStatusExtensions.FromStorage(99));
    }

    [Fact]
    public void VideoExtensions_AcceptTheSupportedContainers()
    {
        foreach (var extension in new[] { "mp4", "flv", "mov", "webm", "vp9", "mkv", "avi", "wmv", "m2ts", "mpg" })
        {
            Assert.True(VideoExtensions.IsVideoExtensionPresent(extension));
            Assert.True(VideoExtensions.IsVideoExtensionPresent(extension.ToUpperInvariant()));
        }

        Assert.False(VideoExtensions.IsVideoExtensionPresent("srt"));
        Assert.False(VideoExtensions.IsVideoExtensionPresent("jpg"));
        Assert.False(VideoExtensions.IsVideoExtensionPresent(""));
        Assert.False(VideoExtensions.IsVideoExtensionPresent(null));
    }
}

public sealed class PageRequestTests
{
    private static PageRequest Parse(string queryString) =>
        PageRequest.Parse(new QueryCollection(QueryHelpers.ParseQuery(queryString)), 20, "pkid", true);

    [Fact]
    public void Parse_UsesTheDefaultPageSizeAndSort()
    {
        var page = Parse(string.Empty);

        Assert.Equal(0, page.Page);
        Assert.Equal(20, page.Size);
        Assert.Equal("pkid", Assert.Single(page.Sorts).Property);
        Assert.True(page.Sorts[0].Descending);
    }

    [Theory]
    [InlineData("?page=3&size=50", 3, 50)]
    [InlineData("?page=-1&size=0", 0, 1)]
    [InlineData("?page=abc&size=abc", 0, 20)]
    [InlineData("?size=2500", 0, PageRequest.MaxSize)]
    public void Parse_ClampsThePagingParameters(string query, int expectedPage, int expectedSize)
    {
        var page = Parse(query);

        Assert.Equal(expectedPage, page.Page);
        Assert.Equal(expectedSize, page.Size);
    }

    [Fact]
    public void Parse_ReadsTheSpringSortSyntax()
    {
        Assert.Equal(
            [("name", false), ("pkid", true)],
            Parse("?sort=name,asc,pkid,desc").Sorts
                .Select(sort => (sort.Property, sort.Descending)));
    }

    [Fact]
    public void SpringPageMetadata_IsConsistentWithTheContent()
    {
        var page = SpringPageFactory.Create(
            new PagedResult<string>(["a", "b"], 0, 2, 5),
            [new SortOrder("pkid", true)]);

        Assert.Equal(2, page.Page.Size);
        Assert.Equal(0, page.Page.Number);
        Assert.Equal(5, page.Page.TotalElements);
        Assert.Equal(3, page.Page.TotalPages);
        Assert.Equal(2, page.Page.NumberOfElements);
        Assert.True(page.Page.First);
        Assert.False(page.Page.Last);
        Assert.False(page.Page.Empty);
        Assert.True(page.Page.Sort.Sorted);
        Assert.Equal("pkid", Assert.Single(page.Page.Sort.Sort).Property);
        Assert.Equal("DESC", page.Page.Sort.Sort[0].Direction);

        var last = SpringPageFactory.Create(new PagedResult<string>(["e"], 2, 2, 5), []);
        Assert.True(last.Page.Last);
    }
}

public sealed class FilterValueConverterTests
{
    [Fact]
    public void Convert_ParsesTheSupportedKinds()
    {
        Assert.Equal("text", FilterValueConverter.Convert(FilterKind.Text, "text"));
        Assert.Equal(42, FilterValueConverter.Convert(FilterKind.Integer, "42"));
        Assert.Equal(9_000_000_000L, FilterValueConverter.Convert(FilterKind.Long, "9000000000"));
        Assert.Equal(true, FilterValueConverter.Convert(FilterKind.Boolean, "true"));
        Assert.Equal(LiveStatus.Stopped, FilterValueConverter.Convert(FilterKind.LiveStatus, "STOPPED"));
        Assert.Equal(LiveStatus.Live, FilterValueConverter.Convert(FilterKind.LiveStatus, "0"));
    }

    [Theory]
    [InlineData(FilterKind.Integer, "not-a-number")]
    [InlineData(FilterKind.Long, "not-a-number")]
    [InlineData(FilterKind.Boolean, "maybe")]
    public void Convert_ThrowsTheOrbisQueryExceptionOfTheDynamicSpecification(FilterKind kind, string value)
    {
        Assert.Throws<OrbisQueryException>(() => FilterValueConverter.Convert(kind, value));
    }
}

public sealed class AutoCleanupServiceTests
{
    [Fact]
    public async Task DelayAsync_TakesAnIntervalOfMonthsWithoutStoppingTheApplication()
    {
        // Two months: past what Task.Delay takes, which threw and stopped the host, live and all.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AutoCleanupService.DelayAsync(TimeSpan.FromDays(60), stop.Token));
    }
}

public sealed class DatabaseBootstrapperTests
{
    [Fact]
    public void StartAsync_CreatesOneDefaultConfigurationPerPlatform()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();

        var twitch = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Twitch"));
        var youtube = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Youtube"));

        Assert.Equal("libx264", twitch.VideoCodecName);
        Assert.Equal(27, twitch.VideoCodec);
        Assert.Equal(6_000_000, twitch.VideoBitrate);
        Assert.Equal("flv", twitch.VideoFormat);
        Assert.Equal(2, twitch.GopSize);
        Assert.Equal(86018, twitch.AudioSetting!.AudioCodec);
        Assert.Equal(160_000, twitch.AudioSetting.AudioBitrate);
        Assert.NotEqual(twitch.Id, youtube.Id);

        // The low CPU preset sits next to the default without being one: the wizard picks the default.
        var lowCpu = settings.FindByTitleAndPlatform("Default Low Twitch", "Twitch");
        Assert.NotNull(lowCpu);
        Assert.False(lowCpu.IsDefaultConfiguration);
    }

    [Fact]
    public void StartAsync_SeedsKickAndFacebookGamingWithTheirOwnDefaults()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();

        // Kick: 1080p30 at 6 Mbps with the 160 Kbps of sound it asks for.
        var kick = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Kick"));
        Assert.Equal("Default Kick", kick.Title);
        Assert.Equal((1920, 1080, 30d), (kick.VideoWidth, kick.VideoHeight, kick.FrameRate));
        Assert.Equal(6_000_000, kick.VideoBitrate);
        Assert.Equal(160_000, kick.AudioSetting!.AudioBitrate);
        Assert.Equal(2, kick.GopSize);

        // Facebook Gaming: 1080p30 in the middle of the 3-6 Mbps Facebook takes for it, main profile.
        var facebook = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Facebook Gaming"));
        Assert.Equal("Default Facebook Gaming", facebook.Title);
        Assert.Equal((1920, 1080, 30d), (facebook.VideoWidth, facebook.VideoHeight, facebook.FrameRate));
        Assert.Equal(4_500_000, facebook.VideoBitrate);
        Assert.Equal(128_000, facebook.AudioSetting!.AudioBitrate);
        Assert.Contains(facebook.VideoSettingsOptions, option => option.Key == "profile" && option.Value == "main");

        var lowKick = settings.FindByTitleAndPlatform("Default Low Kick", "Kick");
        var lowFacebook = settings.FindByTitleAndPlatform("Default Low Facebook Gaming", "Facebook Gaming");
        Assert.NotNull(lowKick);
        Assert.NotNull(lowFacebook);
        Assert.False(lowKick.IsDefaultConfiguration);
        Assert.False(lowFacebook.IsDefaultConfiguration);
        Assert.Equal((1280, 720, 3_000_000), (lowFacebook.VideoWidth, lowFacebook.VideoHeight, lowFacebook.VideoBitrate));
    }

    [Fact]
    public async Task StartAsync_AddsTheNewPlatformsOnceToAnInstallationThatHadTheOldOnes()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();

        // A second start, as every start of the application after the first one is.
        await new DatabaseBootstrapper(
                database.ConnectionFactory,
                settings,
                database.Repository<VideoRepository>(),
                NullLogger<DatabaseBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);

        foreach (var platform in new[] { "Twitch", "Youtube", "Kick", "Facebook Gaming" })
        {
            Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration(platform));
        }
    }

    [Fact]
    public void Schema_TurnsTheFfmpegDeliveryOnForYouTubeOnceAndOffForTwitch()
    {
        using var database = new TemporaryDatabase();
        using var connection = database.ConnectionFactory.Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO setting (stream_url, stream_key, platform_stream_name, description, video_folder, is_active, channel_name, ffmpeg_sender)
                VALUES ('rtmps://a.rtmps.youtube.com/live2', 'yt', 'youtube', '', '', 1, 'yt', 0),
                       ('rtmp://live.twitch.tv/app', 'tw', 'twitch', '', '', 1, 'tw', 1);
                """;
            insert.ExecuteNonQuery();
        }

        Assert.True(DatabaseSchema.PublishYouTubeWithFfmpeg(connection) >= 1);

        var settings = database.Repository<SettingRepository>();
        Assert.True(settings.FindByStreamUrlAndStreamKey("rtmps://a.rtmps.youtube.com/live2", "yt")!.FfmpegSender);
        Assert.False(settings.FindByStreamUrlAndStreamKey("rtmp://live.twitch.tv/app", "tw")!.FfmpegSender);

        // Once: the schema is already past it, so a YouTube channel turned off stays off.
        using (var off = connection.CreateCommand())
        {
            off.CommandText = "UPDATE setting SET ffmpeg_sender = 0 WHERE stream_key = 'yt'";
            off.ExecuteNonQuery();
        }

        DatabaseSchema.EnsureCreated(connection);
        Assert.False(settings.FindByStreamUrlAndStreamKey("rtmps://a.rtmps.youtube.com/live2", "yt")!.FfmpegSender);
    }

    [Fact]
    public void StartAsync_SeedsYouTubeAtAFixedPictureWithNothingThatReplacesTheConstantRate()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();

        var youtube = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Youtube"));
        Assert.Equal((1920, 1080, 30d), (youtube.VideoWidth, youtube.VideoHeight, youtube.FrameRate));
        Assert.Equal(6_000_000, youtube.VideoBitrate);
        Assert.Equal(128_000, youtube.AudioSetting!.AudioBitrate);
        Assert.DoesNotContain(youtube.VideoSettingsOptions, option => option.Key is "tune" or "x264-params");

        var low = settings.FindByTitleAndPlatform("Default Low Youtube", "Youtube")!;
        Assert.Equal((1280, 720, 3_000_000), (low.VideoWidth, low.VideoHeight, low.VideoBitrate));
    }

    [Fact]
    public async Task StartAsync_MovesTheLegacyYouTubeDefaultsButNotOnesTheUserEdited()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();

        // The high default exactly as the earlier versions seeded it.
        var high = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Youtube"));
        high.VideoWidth = null;
        high.VideoHeight = null;
        high.FrameRate = null;
        high.VideoBitrate = 8_000_000;
        high.AudioSetting!.AudioBitrate = 192_000;
        high.VideoSettingsOptions =
        [
            new VideoSettingsOptionEntity { Key = "preset", Value = "veryfast" },
            new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" },
            new VideoSettingsOptionEntity { Key = "profile", Value = "high" },
            new VideoSettingsOptionEntity { Key = "x264-params", Value = "rc_lookahead=20" }
        ];
        settings.Update(high);

        // The low one, edited by the user: a bitrate of their own.
        var low = settings.FindByTitleAndPlatform("Default Low Youtube", "Youtube")!;
        low.VideoBitrate = 2_000_000;
        settings.Update(low);

        await new DatabaseBootstrapper(
                database.ConnectionFactory,
                settings,
                database.Repository<VideoRepository>(),
                NullLogger<DatabaseBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);

        var moved = settings.FindById(high.Id!.Value)!;
        Assert.Equal((1920, 1080, 6_000_000), (moved.VideoWidth, moved.VideoHeight, moved.VideoBitrate));
        Assert.Equal(128_000, moved.AudioSetting!.AudioBitrate);
        Assert.DoesNotContain(moved.VideoSettingsOptions, option => option.Key is "tune" or "x264-params");

        Assert.Equal(2_000_000, settings.FindById(low.Id!.Value)!.VideoBitrate);
    }

    [Fact]
    public async Task StartAsync_RenamesThePlaceholderDefaultInsteadOfAddingAnother()
    {
        // An installation from the first versions: its default is called "test", and it is still the
        // default the user streams with.
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();
        var original = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Twitch"));
        original.Title = "test";
        settings.Update(original);

        await new DatabaseBootstrapper(
                database.ConnectionFactory,
                settings,
                database.Repository<VideoRepository>(),
                NullLogger<DatabaseBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);

        var twitch = Assert.Single(settings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Twitch"));
        Assert.Equal(original.Id, twitch.Id);
        Assert.Equal("Default Twitch", twitch.Title);
    }

    [Fact]
    public async Task StartAsync_KeepsASingleDefaultConfigurationPerPlatform()
    {
        using var database = new TemporaryDatabase();
        var bootstrapper = new DatabaseBootstrapper(
            database.ConnectionFactory,
            database.Repository<VideoSettingRepository>(),
            database.Repository<VideoRepository>(),
            NullLogger<DatabaseBootstrapper>.Instance);

        var duplicates = database.Repository<VideoSettingRepository>().Insert(new VideoSettingEntity
        {
            Title = "Twitch",
            VideoCodec = 27,
            VideoCodecName = "libx264",
            PixelFormat = 0,
            VideoBitrate = 5_000_000,
            VideoFormat = "flv",
            GopSize = 2,
            IsVideoAndAudioSettingActive = true,
            IsDefaultConfiguration = true,
            DefaultPlatformConfiguration = "Twitch"
        });

        await bootstrapper.StartAsync(CancellationToken.None);

        var twitch = database.Repository<VideoSettingRepository>()
            .FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Twitch");
        Assert.Single(twitch);
        Assert.NotEqual(duplicates, Assert.Single(twitch).Id);
    }
}

public sealed class FfmpegCodecCatalogTests
{
    // The database stores FFmpeg enum values (AV_PIX_FMT_*, AV_CODEC_ID_*): the names must match them.
    [Theory]
    [InlineData(0, "yuv420p")]
    [InlineData(1, "yuyv422")]
    [InlineData(4, "yuv422p")]
    [InlineData(23, "nv12")]
    [InlineData(null, "yuv420p")]
    public void PixelFormats_FollowTheFfmpegEnum(int? id, string expected) =>
        Assert.Equal(expected, FfmpegCodecCatalog.ResolvePixelFormat(id));

    [Theory]
    [InlineData(86017, "libmp3lame")]
    [InlineData(86018, "aac")]
    [InlineData(86019, "ac3")]
    [InlineData(86076, "libopus")]
    [InlineData(null, "aac")]
    public void AudioCodecs_FollowTheFfmpegEnum(int? id, string expected) =>
        Assert.Equal(expected, FfmpegCodecCatalog.ResolveAudioCodecName(id));

    [Fact]
    public void VideoCodecName_WinsOverTheNumericCodec()
    {
        Assert.Equal("h264_nvenc", FfmpegCodecCatalog.ResolveVideoCodecName(27, "h264_nvenc"));
        Assert.Equal("libx265", FfmpegCodecCatalog.ResolveVideoCodecName(173, null));
    }
}

public sealed class UserPathTests
{
    [Theory]
    [InlineData("\"C:\\Users\\me\\Downloads\\PS4 live.mp4\"", "C:\\Users\\me\\Downloads\\PS4 live.mp4")]
    [InlineData("  \"/videos/clip.mp4\" ", "/videos/clip.mp4")]
    [InlineData("/videos/clip.mp4", "/videos/clip.mp4")]
    public void NormalizeUserPath_StripsCopyAsPathQuotes(string input, string expected) =>
        Assert.Equal(expected, Orbis.Stream.Core.Services.StreamingService.NormalizeUserPath(input));
}

public sealed class LiveLinkTests
{
    private const string Twitch = "rtmp://live.twitch.tv/app";
    private const string YouTube = "rtmps://a.rtmps.youtube.com/live2";

    [Fact]
    public void PlatformOf_ReadsTheIngestOfTheLive()
    {
        Assert.Equal("twitch", LiveLinkView.PlatformOf(Twitch));
        Assert.Equal("youtube", LiveLinkView.PlatformOf(YouTube));
    }

    [Theory]
    [InlineData("rtmp://live-double.twitch.tv/app:80")]
    [InlineData("rtmp://my.cdn.example/live")]
    [InlineData("")]
    [InlineData(null)]
    public void PlatformOf_HasNoAnswerForAnIngestItDoesNotKnow(string? streamUrl) =>
        Assert.Null(LiveLinkView.PlatformOf(streamUrl));

    [Fact]
    public void UrlOf_TwitchIsTheChannelPage()
    {
        Assert.Equal("https://www.twitch.tv/reproChannel", LiveLinkView.UrlOf(Twitch, "reproChannel", null));
        Assert.Equal("https://www.twitch.tv/reproChannel", LiveLinkView.UrlOf(Twitch, "  reproChannel ", null));
    }

    [Fact]
    public void UrlOf_YouTubeIsTheLiveControlRoomOfTheBroadcastOnAir()
    {
        Assert.Equal(
            "https://studio.youtube.com/video/VlzeeXA0sHI/livestreaming",
            LiveLinkView.UrlOf(YouTube, "madajeeita207", null, "VlzeeXA0sHI"));
    }

    [Fact]
    public void UrlOf_YouTubeBeforeTheBroadcastIsSeenIsTheControlRoomOfTheChannel()
    {
        // A channel id is an address Studio takes; a handle is not, so Studio is asked for the
        // channel of whoever is signed in.
        Assert.Equal(
            "https://studio.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw/livestreaming",
            LiveLinkView.UrlOf(YouTube, "UCuAXFkgsw1L7xaCfnd5JJOw", null));
        Assert.Equal("https://studio.youtube.com/channel/UC/livestreaming", LiveLinkView.UrlOf(YouTube, "@madajeeita207", null));
    }

    [Fact]
    public void YouTubeChannelUrl_IsThePageOfTheHandleOrOfTheId()
    {
        // The page of a handle is /@handle. /channel/ takes a channel id and nothing else, so
        // youtube.com/channel/madajeeita207 is not the channel of that handle: it is a page that is
        // not there, which is a link that looks right and goes nowhere.
        Assert.Equal("https://www.youtube.com/@madajeeita207", LiveLinkView.YouTubeChannelUrl("madajeeita207"));
        Assert.Equal("https://www.youtube.com/@madajeeita207", LiveLinkView.YouTubeChannelUrl("@madajeeita207"));
        Assert.Equal("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw", LiveLinkView.YouTubeChannelUrl("UCuAXFkgsw1L7xaCfnd5JJOw"));
    }

    [Fact]
    public void UrlOf_EscapesWhatWouldBreakTheAddress() =>
        Assert.Equal("https://www.twitch.tv/a%20b", LiveLinkView.UrlOf(Twitch, "a b", null));

    [Theory]
    [InlineData("rtmp://my.cdn.example/live", "reproChannel")]
    [InlineData(Twitch, "")]
    [InlineData(Twitch, "   ")]
    [InlineData(Twitch, null)]
    public void UrlOf_HasNoLinkWithoutAPlatformOrAChannel(string? streamUrl, string? channelName) =>
        Assert.Null(LiveLinkView.UrlOf(streamUrl, channelName, null));

    [Fact]
    public void UrlOf_TakesTheChannelOfTheConfigurationNotThePlatform()
    {
        // The form of the configuration stores the platform in the field named platform stream
        // name, and the channel in the channel name: following the wrong one gave twitch.tv/twitch.
        Assert.Equal("https://www.twitch.tv/ciclovisione", LiveLinkView.UrlOf(Twitch, "ciclovisione", "twitch"));
        Assert.Equal("https://studio.youtube.com/channel/UC/livestreaming", LiveLinkView.UrlOf(YouTube, "ciclovisione", "youtube"));
    }

    [Fact]
    public void UrlOf_FallsBackToTheNameOfTheApiWhenTheChannelIsNotThere()
    {
        // A live started through the API names the channel in the platform stream name.
        Assert.Equal("https://www.twitch.tv/channel-e2e-row", LiveLinkView.UrlOf(Twitch, null, "channel-e2e-row"));
    }

    [Fact]
    public void LabelAndMark_AreTheOnesOfTheConfigurationForm()
    {
        Assert.Equal("Twitch", LiveLinkView.LabelOf("twitch"));
        Assert.Equal("YouTube", LiveLinkView.LabelOf("youtube"));
        Assert.Null(LiveLinkView.LabelOf("anythingElse"));
    }

    [Fact]
    public void EachPlatformHasItsOwnMarkAndItsOwnColour()
    {
        // The brand of the platform, not a glyph of an icon font that happens to look like it: a
        // channel row is read at a glance by which mark it carries.
        Assert.NotEqual(LiveLinkView.MarkupOf("twitch"), LiveLinkView.MarkupOf("youtube"));
        Assert.NotEqual(LiveLinkView.MarkupOf("twitch"), LiveLinkView.MarkupOf("anythingElse"));

        // The Twitch mark is the outline of the speech bubble with the two bars; the YouTube one the
        // rounded rectangle with the play triangle. Both are paths, so a page can draw them itself.
        Assert.StartsWith("M11.571 4.714", LiveLinkView.MarkupOf("twitch"), StringComparison.Ordinal);
        Assert.StartsWith("M23.498 6.186", LiveLinkView.MarkupOf("youtube"), StringComparison.Ordinal);

        // A platform the application does not know gets the plain play, in the colour of the text.
        Assert.StartsWith("M8 5v14", LiveLinkView.MarkupOf("anythingElse"), StringComparison.Ordinal);
        Assert.StartsWith("M8 5v14", LiveLinkView.MarkupOf(null), StringComparison.Ordinal);

        Assert.Equal("platform-twitch", LiveLinkView.ClassOf("twitch"));
        Assert.Equal("platform-youtube", LiveLinkView.ClassOf("youtube"));
        Assert.Equal("platform-generic", LiveLinkView.ClassOf("anythingElse"));
    }

    private const string Kick = "rtmps://fa723fc1b171.global-contribute.live-video.net/app";
    private const string Facebook = "rtmps://rtmp-api.facebook.com:443/rtmp";

    [Theory]
    // The presets of the channel settings...
    [InlineData(Kick, "kick")]
    [InlineData(Facebook, "facebook")]
    // As the dashboards write them: with the slash at the end.
    [InlineData(Facebook + "/", "facebook")]
    [InlineData("rtmp://live.twitch.tv/app/", "twitch")]
    // ...and the ingest an account or a live has of its own, which is never the preset.
    [InlineData("rtmps://0123456789ab.global-contribute.live-video.net:443/app/", "kick")]
    [InlineData("rtmps://live-api-s.facebook.com:443/rtmp/", "facebook")]
    public void PlatformOf_KnowsKickAndFacebookByTheDomainOfTheirIngest(string streamUrl, string platform) =>
        Assert.Equal(platform, LiveLinkView.PlatformOf(streamUrl));

    [Fact]
    public void PlatformOf_StillWantsThePresetForTheIngestsThatAreTheSameForEverybody()
    {
        // A Twitch server of its own is not the address the configuration form fills in.
        Assert.Null(LiveLinkView.PlatformOf("rtmps://fra05.contribute.live-video.net/app"));
    }

    [Fact]
    public void UrlOf_KickIsTheChannelAndFacebookGamingThePage()
    {
        Assert.Equal("https://kick.com/reprochannel", LiveLinkView.UrlOf(Kick, "ReproChannel", "kick"));
        Assert.Equal("https://www.facebook.com/ReproGaming", LiveLinkView.UrlOf(Facebook, "ReproGaming", "facebook"));
        Assert.Equal("https://www.facebook.com/repro", LiveLinkView.UrlOf("rtmps://live-api-s.facebook.com:443/rtmp/", "@repro", null));
    }

    [Fact]
    public void KickAndFacebookGamingHaveTheirNamesMarksAndColours()
    {
        Assert.Equal("Kick", LiveLinkView.LabelOf("kick"));
        Assert.Equal("Facebook Gaming", LiveLinkView.LabelOf("facebook"));

        // The K of blocks, and the F of two blocks.
        Assert.StartsWith("M1.333 0h8", LiveLinkView.MarkupOf("kick"), StringComparison.Ordinal);
        Assert.StartsWith("M0 0v24h15.67", LiveLinkView.MarkupOf("facebook"), StringComparison.Ordinal);
        var marks = new[] { "twitch", "youtube", "kick", "facebook", null }.Select(LiveLinkView.MarkupOf).ToList();
        Assert.Equal(marks.Count, marks.Distinct().Count());

        Assert.Equal("platform-kick", LiveLinkView.ClassOf("kick"));
        Assert.Equal("platform-facebook", LiveLinkView.ClassOf("facebook"));
    }

    [Fact]
    public void TheChannelSettingsOfferTheFourPlatformsWithTheirIngest()
    {
        Assert.Equal(["twitch", "youtube", "kick", "facebook"], MainChannelSettingModel.Platforms.Select(platform => platform.Value));
        Assert.All(MainChannelSettingModel.Platforms, platform =>
        {
            Assert.StartsWith("rtmp", platform.StreamUrl, StringComparison.Ordinal);
            // The preset is an ingest of the platform it is offered for.
            Assert.Equal(platform.Platform, Orbis.Stream.Core.Streaming.StreamPlatforms.Detect(platform.StreamUrl + "/key"));
        });

        // Kick and Facebook are the two whose address can be the account's or the live's own.
        Assert.Equal("kick facebook", MainChannelSettingModel.PersonalIngests);
    }

    [Fact]
    public void ChoiceOf_ReadsTheNameThenTheIngestOfAConfiguration()
    {
        Assert.Equal("facebook", MainChannelSettingModel.ChoiceOf("Facebook")?.Value);
        Assert.Equal("kick", MainChannelSettingModel.ChoiceOf(" kick ")?.Value);
        Assert.Equal("kick", MainChannelSettingModel.ChoiceOf("my channel", Kick + "/")?.Value);
        Assert.Equal("youtube", MainChannelSettingModel.ChoiceOf(null, "rtmps://a.rtmps.youtube.com/live2")?.Value);
        Assert.Null(MainChannelSettingModel.ChoiceOf("my channel", "rtmp://my.cdn.example/live"));
    }

    [Fact]
    public void TheLowDefaultsAreShownInTheLanguageOfThePage()
    {
        var text = new Orbis.Stream.Core.I18n.UiText(new Orbis.Stream.Core.I18n.Localizer(
            new Orbis.Stream.Core.I18n.MessageCatalog(
                Path.Combine(AppContext.BaseDirectory, "Messages"), NullLogger<Orbis.Stream.Core.I18n.MessageCatalog>.Instance),
            new HttpContextAccessor()));

        Assert.Equal("Default Low Kick", SettingTitleView.Of("Default Low Kick", text));
        // A name with a space is a key without it.
        Assert.Equal("Default Low Facebook Gaming", SettingTitleView.Of("Default Low Facebook Gaming", text));
        Assert.Equal(text["defaultLowFacebookGaming"], SettingTitleView.Of("default low facebook gaming", text));
        // The seed spells YouTube the way it was stored, and is shown the way it is spelled.
        Assert.Equal("Default Low YouTube", SettingTitleView.Of("Default Low Youtube", text));
        Assert.Equal("Default Kick", SettingTitleView.Of("Default Kick", text));
        Assert.Equal("Mine", SettingTitleView.Of("Mine", text));
        Assert.Equal(text["untitled"], SettingTitleView.Of("  ", text));
    }
}

public sealed class SystemFactTests
{
    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(1023UL, "1023 B")]
    [InlineData(1024UL, "1 kB")]
    [InlineData(1536UL, "1.5 kB")]
    [InlineData(1073741824UL, "1 GB")]
    [InlineData(34359738368UL, "32 GB")]
    [InlineData(3817868576UL, "3.6 GB")]
    public void FormatBytes_UsesTheBinaryDivisorAndNoLocale(ulong bytes, string expected) =>
        Assert.Equal(expected, SystemFact.FormatBytes(bytes));

    [Fact]
    public void FormatUptime_IsReadableWithoutAnyLanguage() =>
        Assert.Equal("3.05:04:05", SystemFact.FormatUptime(new TimeSpan(3, 5, 4, 5)));

    [Fact]
    public void FormatUptime_OfAClockThatWentBackwardsIsZero()
    {
        Assert.Equal("0.00:00:00", SystemFact.FormatUptime(TimeSpan.FromSeconds(-90)));
    }

    [Fact]
    public void Facts_DescribeTheMachineThatRunsThem()
    {
        var facts = new PortableSystemInfoProvider().GetFacts().ToDictionary(fact => fact.Key, fact => fact.Value);

        Assert.NotEmpty(facts[SystemFact.Os]);
        Assert.NotEmpty(facts[SystemFact.Computer]);
        Assert.Equal(Environment.ProcessorCount.ToString(), facts[SystemFact.CpuCores].Split(' ')[0]);
        Assert.False(string.IsNullOrWhiteSpace(facts[SystemFact.Dotnet]));
    }

    [Fact]
    public void Facts_NeverCarryAnEmptyValue()
    {
        var facts = new PortableSystemInfoProvider().GetFacts();

        Assert.NotEmpty(facts);
        Assert.All(facts, fact =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fact.Key));
            Assert.False(string.IsNullOrWhiteSpace(fact.Value));
        });
        Assert.Equal(facts.Select(fact => fact.Key).Distinct().Count(), facts.Count);
    }
}

public sealed class TemperatureTests
{
    [Fact]
    public void Temperatures_AnswerAPlausibleReadingOrNoneAtAll()
    {
        // The contract every sensor obeys: a temperature a machine of this size could really be at,
        // or -1 for a machine that has no sensor to read. Nothing in between, nothing invented.
        var provider = new PortableSystemInfoProvider();

        foreach (var celsius in new[] { provider.GetCpuTemperature(), provider.GetGpuTemperature() })
        {
            Assert.True(
                celsius == SystemInfoProviderFactory.NotAvailable || (celsius is >= 1 and <= 120),
                $"a sensor answered {celsius}");
        }
    }

    [Fact]
    public void Temperatures_AreReadOnceForAsManyAsksAsThereAre()
    {
        // A sensor is expensive: a WMI query costs tens of milliseconds and the tool of the driver
        // is a process of its own, while the meters are pushed once a second. Two asks in a row
        // must not be two readings, or the push of the channel would pay for them every time.
        var reads = 0;
        var cache = new TemperatureCache();

        Assert.Equal((41, 57), cache.Read(() => Counted(41), () => Counted(57)));
        Assert.Equal((41, 57), cache.Read(() => Counted(99), () => Counted(99)));
        Assert.Equal(2, reads);

        int Counted(int celsius)
        {
            reads++;
            return celsius;
        }
    }

    [Fact]
    public void AMachineWithoutSensorsIsNotAskedAgainEverySecond()
    {
        // -1 for both is a machine that has no sensor at all, and that answer is kept longer than a
        // reading: a missing sensor must cost nothing once it has been found to be missing.
        var reads = 0;
        var cache = new TemperatureCache();

        for (var ask = 0; ask < 5; ask++)
        {
            cache.Read(Missing, Missing);
        }

        Assert.Equal(2, reads);

        int Missing()
        {
            reads++;
            return SystemInfoProviderFactory.NotAvailable;
        }
    }
}

public sealed class LiveChangeNotifierTests
{
    [Fact]
    public void EveryChangeHasItsOwnNumber()
    {
        var notifier = new LiveChangeNotifier();

        Assert.Equal(0, notifier.Version);

        notifier.Raise();
        notifier.Raise();

        Assert.Equal(2, notifier.Version);
    }

    [Fact]
    public async Task AListenerWaitsForAChangeAndIsToldWhichOneItIs()
    {
        var notifier = new LiveChangeNotifier();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var waiting = notifier.WaitAsync(0, cancel.Token);
        Assert.False(waiting.IsCompleted);

        notifier.Raise();
        notifier.Raise();

        // Both changes went by while it was waiting: one wake up, at the number it reached.
        Assert.Equal(2, await waiting);
    }

    [Fact]
    public async Task AListenerThatArrivesLateIsAnsweredAtOnce()
    {
        var notifier = new LiveChangeNotifier();
        notifier.Raise();

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The change it missed while it was connecting is the one it asks for, and waiting would
        // leave it looking at rows that are already out of date until the next change comes.
        Assert.Equal(1, await notifier.WaitAsync(0, cancel.Token));
    }

    [Fact]
    public async Task AListenerResumesFromTheChangeItAlreadyHas()
    {
        var notifier = new LiveChangeNotifier();
        notifier.Raise();
        notifier.Raise();

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // It already has the first two changes, and those are on its screen: it waits for what is
        // next, not for the ones it is not missing.
        var waiting = notifier.WaitAsync(notifier.Version, cancel.Token);
        Assert.False(waiting.IsCompleted);

        notifier.Raise();

        Assert.Equal(3, await waiting);
    }

    [Fact]
    public async Task ManyListenersAreAllWokenByTheSameChange()
    {
        var notifier = new LiveChangeNotifier();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var listeners = Enumerable.Range(0, 5).Select(_ => notifier.WaitAsync(0, cancel.Token)).ToArray();
        notifier.Raise();

        Assert.All(await Task.WhenAll(listeners), version => Assert.Equal(1, version));
    }

    [Fact]
    public async Task AChangeThatArrivesBetweenTheTestAndTheWaitIsNotLost()
    {
        var notifier = new LiveChangeNotifier();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The window that loses a change is the one between having read the number and starting to
        // wait for the next: the change is then already there, and the answer has to say so.
        for (var round = 0; round < 50; round++)
        {
            var since = notifier.Version;
            var waiting = notifier.WaitAsync(since, cancel.Token);
            notifier.Raise();

            Assert.Equal(since + 1, await waiting);
        }
    }

    [Fact]
    public async Task AListenerThatGoesAwayDoesNotHoldTheOnesThatStay()
    {
        var notifier = new LiveChangeNotifier();
        using var staying = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = notifier.WaitAsync(0, staying.Token);

        using var leaving = new CancellationTokenSource();
        var gone = notifier.WaitAsync(0, leaving.Token);
        leaving.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);
        Assert.False(waiting.IsCompleted);

        notifier.Raise();

        Assert.Equal(1, await waiting);
    }
}
