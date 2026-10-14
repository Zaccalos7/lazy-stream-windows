using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>The delivery and encoder settings of a live sent to X.</summary>
public sealed class XDeliveryTests
{


    private const string X = "rtmps://de.pscp.tv:443/x/abcdefghijkl";

    private static readonly MediaProbeResult FullHdFile = new(1920, 1080, 30d, true, 2, 60d);

    private static VideoSettingEntity Setting(int? gopSize = 2, params (string Key, string Value)[] options) => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 4_500_000,
        VideoFormat = "flv",
        GopSize = gopSize,
        VideoWidth = 1920,
        VideoHeight = 1080,
        FrameRate = 30,
        VideoSettingsOptions = options.Select(option => new VideoSettingsOptionEntity { Key = option.Key, Value = option.Value }).ToList(),
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    private static string Command(string ingest, StreamPlatformProfile profile, VideoSettingEntity setting) =>
        string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", ingest, FullHdFile, setting, Profile: profile)));

    [Fact]
    public void XHasADeliveryOfItsOwnWithNoBurstAndAKeyframeEveryThreeSeconds()
    {
        var x = StreamPlatformProfile.X;

        Assert.Same(x, StreamPlatformProfile.For(X));
        Assert.Same(x, StreamPlatformProfile.For("rtmp://va.pscp.tv:80/x/abcdefghijkl"));
        Assert.Equal(StreamPlatform.X, x.Platform);
        Assert.Equal(RelayPacing.Sender, x.Pacing);
        Assert.Equal(RelayTransport.FfmpegSender, x.Transport);
        // No head start: the ingest of X closes a publish that arrives faster than real time.
        Assert.Equal(TimeSpan.Zero, x.Preroll);
        Assert.Same(StreamPlatformProfile.YouTube.Adaptation, x.Adaptation);
        Assert.True(x.UniformFormat);
        Assert.True(x.RequiresAudio);
        Assert.False(x.ChoosableTransport);
        Assert.Equal(3, x.KeyframeSeconds);
        Assert.Equal(44_100, x.AudioSampleRate);
    }

    [Fact]
    public void ALiveToXCarriesAKeyframeEveryThreeSeconds()
    {
        // The defaults of X name three seconds: 90 frames at 30 fps, pinned on the clock.
        var named = Command(X, StreamPlatformProfile.X, Setting(gopSize: 3));
        Assert.Contains("-g 90", named, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*3)", named, StringComparison.Ordinal);
        Assert.Contains("-ar 44100", named, StringComparison.Ordinal);
        // No constant rate is asked of X: the bitrate is a ceiling, as on Twitch.
        Assert.DoesNotContain("-minrate", named, StringComparison.Ordinal);

        // A setting that names no interval gets the one of the platform.
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*3)", Command(X, StreamPlatformProfile.X, Setting(gopSize: null)), StringComparison.Ordinal);
    }

    [Fact]
    public void XGoesOutThroughAnFfmpegSenderThatReadsAtRealTimeFromTheFirstFrame()
    {
        var output = LiveOutput.For(X, new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger.Instance, RelayTransport.NativeRtmp);

        Assert.Equal(StreamPlatformProfile.X, output!.Profile);
        var arguments = string.Join(' ', FfmpegCommandBuilder.BuildSender(X, output.Profile));
        // Omitting the burst, or writing zero, asks FFmpeg 8.1 for its default half second.
        Assert.Contains("-readrate 1 -readrate_initial_burst 0.000001 -f flv -i pipe:0", arguments, StringComparison.Ordinal);
        Assert.EndsWith(X, arguments, StringComparison.Ordinal);
    }
}

/// <summary>
/// A source of X belongs to the region it was made in: the channel settings ask for the region,
/// and the configuration keeps the RTMPS address of its server.
/// </summary>
public sealed class XIngestRegionTests
{
    [Fact]
    public void XTakesItsSourcesInTwelveRegionsEachOnAServerOfItsOwn()
    {
        Assert.Equal(
            ["ca", "or", "va", "br", "fr", "ie", "de", "au", "in", "jp", "kr", "sg"],
            XIngestRegions.All.Select(region => region.Code));
        Assert.Equal(XIngestRegions.All.Count, XIngestRegions.All.Select(region => region.Key).Distinct().Count());

        foreach (var region in XIngestRegions.All)
        {
            Assert.Equal($"rtmps://{region.Code}.pscp.tv:443/x", region.StreamUrl);
            // Every one of them is X, for the delivery and for the pages.
            Assert.Equal(StreamPlatform.X, StreamPlatforms.Detect(region.StreamUrl + "/key"));
            Assert.Equal("x", LiveLinkView.PlatformOf(region.StreamUrl));
            Assert.Same(region, XIngestRegions.OfUrl(region.StreamUrl));
        }

        // The preset of the channel settings is the region a new configuration starts on.
        Assert.Equal("de", XIngestRegions.Default.Code);
        Assert.Equal(XIngestRegions.Default.StreamUrl, MainChannelSettingModel.ChoiceOf("x")!.StreamUrl);
    }

    [Theory]
    [InlineData("rtmps://de.pscp.tv:443/x", "de")]
    [InlineData("rtmp://va.pscp.tv:80/x", "va")]
    [InlineData("  RTMPS://SG.PSCP.TV:443/x/abcdefghijkl ", "sg")]
    [InlineData("rtmp://zz.pscp.tv:80/x", null)]
    [InlineData("rtmp://video.pscp.tv/x", null)]
    [InlineData("rtmps://a.rtmps.youtube.com/live2", null)]
    [InlineData("https://de.pscp.tv/x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheRegionOfAConfigurationIsReadOffItsAddress(string? streamUrl, string? code) =>
        Assert.Equal(code, XIngestRegions.OfUrl(streamUrl)?.Code);

    [Fact]
    public void ARegionIsFoundByItsCodeWhateverTheCase()
    {
        Assert.Equal("sg", XIngestRegions.Of(" SG ")?.Code);
        Assert.Null(XIngestRegions.Of("zz"));
        Assert.Null(XIngestRegions.Of(null));
    }

    [Fact]
    public void EveryRegionHasItsNameInTheLanguageOfThePage()
    {
        var text = new Orbis.Stream.Core.I18n.UiText(new Orbis.Stream.Core.I18n.Localizer(
            new Orbis.Stream.Core.I18n.MessageCatalog(
                Path.Combine(AppContext.BaseDirectory, "Messages"), NullLogger<Orbis.Stream.Core.I18n.MessageCatalog>.Instance),
            new Microsoft.AspNetCore.Http.HttpContextAccessor()));

        Assert.All(XIngestRegions.All, region => Assert.NotEqual(region.Key, text[region.Key]));
        Assert.Equal("Germany (Frankfurt)", text["xRegionDe"]);
        Assert.NotEqual("xRegion", text["xRegion"]);
    }
}

/// <summary>
/// The pages offer X in the channel settings, the templates of a video setting, and the live
/// wizard, whose choices are laid
/// out in columns with the mark of their platform.
/// </summary>
public sealed class XPageTests : IClassFixture<ApplicationFixture>
{
    private readonly ApplicationFixture _fixture;

    public XPageTests(ApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_channel_settings_offer_X_and_its_source_region()
    {
        var html = await _fixture.Client.GetStringAsync("/orbis/mainChannelSetting?edit=new");

        Assert.Contains("value=\"x\" data-stream-url=\"rtmps://de.pscp.tv:443/x\"", html, StringComparison.Ordinal);
        Assert.Contains("data-show-when=\"kick facebook\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task X_is_asked_for_the_region_of_its_source_instead_of_an_address()
    {
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainChannelSetting?edit=new"));

        // The address box goes away while X is picked, and the list of its regions takes its place.
        Assert.Contains("<label class=\"field\" data-hide-when=\"x\">", html, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field\" data-show-when=\"x\">", html, StringComparison.Ordinal);
        Assert.Contains("<select class=\"input\" name=\"xRegion\">", html, StringComparison.Ordinal);
        Assert.Equal(XIngestRegions.All.Count, Regex.Matches(html, "<option value=\"[a-z]{2}\"[^>]*>[^<]+ · [a-z]{2}\\.pscp\\.tv</option>").Count);

        // A new configuration starts on the region of the preset, each region says its server.
        Assert.Contains("<option value=\"de\" selected=\"selected\">Germany (Frankfurt) · de.pscp.tv</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"va\">US East (Virginia) · va.pscp.tv</option>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configuration_of_X_keeps_the_RTMPS_address_of_its_region_and_opens_on_it_again()
    {
        var token = await TokenAsync("/orbis/mainChannelSetting?edit=new");
        using var saved = await _fixture.NoRedirectClient.PostAsync("/orbis/mainChannelSetting?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["platformStreamName"] = "x",
            ["channelName"] = "region.account",
            // The box is off while X is picked, so nothing is sent from it; the region is what counts.
            ["xRegion"] = "sg",
            ["streamKey"] = "x-source-key-1",
            ["description"] = string.Empty,
            ["autoCleanupEnabled"] = "false",
            ["autoCleanupIntervalMonths"] = "0",
            ["autoCleanupOlderThanMonths"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var configuration = Assert.Single(_fixture.Services.GetRequiredService<SettingRepository>()
            .FindAll(new Dictionary<string, string> { ["channelName"] = "region.account" }));
        Assert.Equal("rtmps://sg.pscp.tv:443/x", configuration.StreamUrl);
        Assert.Equal("x-source-key-1", configuration.StreamKey);

        // Opened again, the list is on the region its address is on.
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync($"/orbis/mainChannelSetting?edit={configuration.Id}"));
        Assert.Contains("<option value=\"sg\" selected=\"selected\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"de\" selected=\"selected\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_region_of_X_is_ignored_for_every_other_platform()
    {
        var token = await TokenAsync("/orbis/mainChannelSetting?edit=new");
        using var saved = await _fixture.NoRedirectClient.PostAsync("/orbis/mainChannelSetting?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["platformStreamName"] = "facebook",
            ["channelName"] = "facebook.region.account",
            ["streamUrl"] = "rtmps://live-api-s.facebook.com:443/rtmp",
            // Sent with the form whatever is picked, and meaningless away from X.
            ["xRegion"] = "sg",
            ["streamKey"] = "abcd-efgh-ijkl",
            ["description"] = string.Empty,
            ["autoCleanupEnabled"] = "false",
            ["autoCleanupIntervalMonths"] = "0",
            ["autoCleanupOlderThanMonths"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var configuration = Assert.Single(_fixture.Services.GetRequiredService<SettingRepository>()
            .FindAll(new Dictionary<string, string> { ["channelName"] = "facebook.region.account" }));
        Assert.Equal("rtmps://live-api-s.facebook.com:443/rtmp", configuration.StreamUrl);
    }

    /// <summary>The antiforgery token of a page, for a post from the client that drew it.</summary>
    private async Task<string> TokenAsync(string page)
    {
        using var response = await _fixture.NoRedirectClient.GetAsync(page);
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"");
        Assert.True(token.Success, "The page draws no antiforgery token.");
        return token.Groups["token"].Value;
    }

    [Fact]
    public async Task The_video_settings_offer_the_template_of_X()
    {
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainVideoSetting?edit=new"));

        Assert.Contains("class=\"btn brand x\" href=\"?edit=new&preset=X\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presets_of_a_retired_platform_are_hidden_and_cannot_prefill_the_editor()
    {
        var repository = _fixture.Services.GetRequiredService<VideoSettingRepository>();
        var original = Assert.Single(repository.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Twitch"));
        var inserted = new List<int>();
        try
        {
            foreach (var title in new[] { "Default Retired service", "Default Low Retired service" })
            {
                var preset = VideoSettingsRequest.FromEntity(original).ToEntity();
                preset.Id = null;
                preset.Title = title;
                preset.DefaultPlatformConfiguration = "Retired service";
                preset.IsDefaultConfiguration = title == "Default Retired service";
                preset.IsVideoAndAudioSettingActive = true;
                inserted.Add(repository.Insert(preset));
            }

            var wizard = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1"));
            Assert.DoesNotContain("Default Retired service", wizard, StringComparison.Ordinal);
            Assert.DoesNotContain("Default Low Retired service", wizard, StringComparison.Ordinal);
            Assert.Contains("<label class=\"choice x\">", wizard, StringComparison.Ordinal);

            var editor = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainVideoSetting?edit=new&preset=Retired%20service"));
            Assert.DoesNotContain("value=\"Default Retired service\"", editor, StringComparison.Ordinal);

            // Historical settings remain available to the rows that used them.
            Assert.All(inserted, id => Assert.NotNull(repository.FindById(id)));
        }
        finally
        {
            foreach (var id in inserted)
            {
                repository.Delete(id);
            }
        }
    }

    [Fact]
    public async Task The_template_of_X_fills_the_setting_with_its_keyframe_of_three_seconds()
    {
        var html = await _fixture.Client.GetStringAsync("/orbis/mainVideoSetting?edit=new&preset=X");

        Assert.Matches("name=\"Form.GopSize\"[^>]*value=\"3\"|value=\"3\"[^>]*name=\"Form.GopSize\"", html);
    }

    [Fact]
    public async Task The_wizard_lays_the_settings_out_in_columns_with_the_mark_of_their_platform()
    {
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1"));

        Assert.Contains("class=\"dialog wizard\" id=\"start-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"dialog wizard\" id=\"playlist-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"choice-list grid section\"", html, StringComparison.Ordinal);

        // The seeded settings of X carry its colour and mark.
        Assert.Contains("<label class=\"choice x\">", html, StringComparison.Ordinal);
        Assert.Contains(LiveLinkView.MarkupOf("x"), html, StringComparison.Ordinal);
        Assert.Contains("1920×1080 · 30 fps · 9 Mbit/s", html, StringComparison.Ordinal);
    }
}
