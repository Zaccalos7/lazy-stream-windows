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
    private const string YouTube = "rtmps://a.rtmp.youtube.com/live2";

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
    public void UrlOf_YouTubeIsThePageOfTheChannel()
    {
        Assert.Equal("https://www.youtube.com/channel/reproChannel", LiveLinkView.UrlOf(YouTube, "reproChannel", null));
        Assert.Equal("https://www.youtube.com/channel/madajeeita207", LiveLinkView.UrlOf(YouTube, "madajeeita207", null));
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
        Assert.Equal("https://www.youtube.com/channel/ciclovisione", LiveLinkView.UrlOf(YouTube, "ciclovisione", "youtube"));
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
