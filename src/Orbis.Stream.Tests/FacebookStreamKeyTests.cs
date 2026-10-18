using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// Facebook keeps the key of a live for a day and not for a moment longer, and the key is written
/// in Facebook Live Producer once the broadcast is standing up: the page links the producer, says
/// that the live is started there first and that the stream key of that broadcast is what goes into
/// the configuration, and says how old the key it keeps is - which is the only thing it can tell,
/// since Facebook cannot be asked whether a key is still good.
/// </summary>
public sealed class FacebookStreamKeyTests : IClassFixture<ApplicationFixture>
{
    private const string Ingest = StreamPlatforms.FacebookIngestUrl;

    private readonly ApplicationFixture _fixture;

    public FacebookStreamKeyTests(ApplicationFixture fixture) => _fixture = fixture;

    private T Service<T>() where T : notnull => _fixture.Services.GetRequiredService<T>();

    private static SettingRequest Configuration(string channel, string? key) =>
        new(Ingest, key, "facebook", string.Empty, null, true, channel, false, 0, 0);

    [Fact]
    public void The_moment_a_key_is_saved_is_stored_with_it()
    {
        var settings = Service<SettingService>();
        var repository = Service<SettingRepository>();

        settings.AddNewConfiguration(Configuration("key.saved.at", "facebook-key-1"));
        var saved = Assert.Single(repository.FindAll(new Dictionary<string, string> { ["channelName"] = "key.saved.at" }));
        Assert.NotNull(saved.StreamKeySavedAt);

        // An edit that does not touch the key leaves the moment alone: editing the name of the
        // channel must not turn a key of this morning into one of this afternoon.
        var stamp = saved.StreamKeySavedAt;
        settings.ModifySetting(saved.Id, Configuration("key.saved.at", null) with { ChannelName = "renamed.channel" });
        var renamed = repository.FindById(saved.Id)!;
        Assert.Equal("renamed.channel", renamed.ChannelName);
        Assert.Equal(stamp, renamed.StreamKeySavedAt);

        // A new key is a new day: the moment is written again.
        settings.ModifySetting(saved.Id, Configuration("renamed.channel", "facebook-key-2"));
        var rekeyed = repository.FindById(saved.Id)!;
        Assert.Equal("facebook-key-2", rekeyed.StreamKey);
        Assert.True(rekeyed.StreamKeySavedAt >= stamp);

        settings.DeleteAStreamingSetting(rekeyed.Id);
    }

    [Fact]
    public void A_Facebook_key_is_good_for_a_day_and_the_key_of_another_platform_is_good_for_good()
    {
        var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Local);

        // Named Facebook, or a Facebook ingest: the page knows both ways of saying it.
        Assert.True(ExpiringStreamKeys.Expires("facebook", null));
        Assert.True(ExpiringStreamKeys.Expires(null, Ingest));
        Assert.True(ExpiringStreamKeys.Expires(null, "rtmps://live-api-s.facebook.com:443/rtmp/stream-123"));

        Assert.False(ExpiringStreamKeys.Expires("twitch", "rtmp://live.twitch.tv/app"));

        // A day old is the end of it, and there is nothing to ask Facebook about it.
        Assert.False(ExpiringStreamKeys.IsExpired("facebook", Ingest, now.AddHours(-23), now));
        Assert.True(ExpiringStreamKeys.IsExpired("facebook", Ingest, now.AddDays(-1), now));
        Assert.True(ExpiringStreamKeys.IsExpired("facebook", Ingest, now.AddDays(-9), now));

        // A key saved before the moment was kept has an age nobody knows, and is taken to be old.
        Assert.True(ExpiringStreamKeys.IsExpired("facebook", Ingest, null, now));

        // A platform that does not expire its keys never has an expired one.
        Assert.False(ExpiringStreamKeys.IsExpired("twitch", "rtmp://live.twitch.tv/app", null, now));
    }

    [Fact]
    public async Task The_wizard_says_step_by_step_how_a_Facebook_live_is_started_and_keyed()
    {
        var settings = Service<SettingService>();
        settings.AddNewConfiguration(Configuration("wizard.facebook.channel", "facebook-key-wizard"));

        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1"));

        // The producer is where the broadcast is started and where its key is written, so that is
        // where the page sends the user, on the address the dashboard itself opens.
        Assert.Contains("https://www.facebook.com/live/producer", html, StringComparison.Ordinal);

        // The four steps, in the order they are asked: the broadcast first, the key out of the
        // producer, the key into the configuration, and the stream from here at the end.
        var steps = TheStepsOf(html);
        Assert.Equal(4, steps.Count);
        Assert.Contains("Go live", steps[0], StringComparison.Ordinal);
        Assert.Contains("Use Stream key", steps[1], StringComparison.Ordinal);
        Assert.Contains("REST API", steps[1], StringComparison.Ordinal);
        Assert.Contains("key", steps[2], StringComparison.Ordinal);
        Assert.Contains("Orbis", steps[3], StringComparison.Ordinal);

        // And the key is good for a day, which is why the wizard watches how old the one it has is.
        Assert.Contains("one day", html, StringComparison.Ordinal);

        settings.DeleteAStreamingSetting(Service<SettingRepository>()
            .FindAll(new Dictionary<string, string> { ["channelName"] = "wizard.facebook.channel" }).Single().Id);
    }

    /// <summary>The steps of the Facebook notice, in the order they are written.</summary>
    private static List<string> TheStepsOf(string html)
    {
        var notice = Regex.Match(html, "<ol class=\"facebook-live-steps\">(.*?)</ol>", RegexOptions.Singleline);
        Assert.True(notice.Success, "the Facebook notice draws no steps at all");
        return Regex.Matches(notice.Groups[1].Value, "<li>(.*?)</li>", RegexOptions.Singleline)
            .Select(step => WebUtility.HtmlDecode(Regex.Replace(step.Groups[1].Value, "<.*?>", string.Empty)).Trim())
            .ToList();
    }

    /// <summary>
    /// A key of yesterday cannot be streamed with, and nothing about it says so: the configuration
    /// row is exactly what it was when the key was fresh. The wizard is where it is caught, and the
    /// row of the configuration says it before the dialog is even opened.
    /// </summary>
    [Fact]
    public async Task The_wizard_and_the_configurations_warn_about_a_key_that_has_expired()
    {
        var settings = Service<SettingService>();
        var repository = Service<SettingRepository>();
        settings.AddNewConfiguration(Configuration("expiring.facebook.channel", "facebook-key-old"));
        var configuration = Assert.Single(repository.FindAll(new Dictionary<string, string> { ["channelName"] = "expiring.facebook.channel" }));

        try
        {
            repository.Update(Expired(configuration));

            var wizard = await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1");
            Assert.Contains("data-facebook-key-expired", wizard, StringComparison.Ordinal);

            var list = await _fixture.Client.GetStringAsync("/orbis/mainChannelSetting");
            Assert.Contains("data-facebook-key-expired", list, StringComparison.Ordinal);

            var editor = await _fixture.Client.GetStringAsync($"/orbis/mainChannelSetting?edit={configuration.Id}");
            Assert.Contains("data-facebook-key-expired", editor, StringComparison.Ordinal);
        }
        finally
        {
            settings.DeleteAStreamingSetting(configuration.Id);
        }

        // A platform whose keys do not expire is never warned about.
        var twitch = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1"));
        Assert.DoesNotContain("has expired", twitch, StringComparison.Ordinal);

        static SettingEntity Expired(SettingEntity setting)
        {
            setting.StreamKeySavedAt = DateTime.Now.AddDays(-2);
            return setting;
        }
    }

    [Fact]
    public async Task The_channel_settings_lead_to_the_producer_and_not_to_a_version_of_it()
    {
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainChannelSetting?edit=new"));

        Assert.Equal("https://www.facebook.com/live/producer", LiveLinkView.FacebookProducerUrl);
        Assert.Contains("href=\"https://www.facebook.com/live/producer\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("facebook.com/live/producer/v2", html, StringComparison.Ordinal);
    }
}