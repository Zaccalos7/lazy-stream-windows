using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Pages;

namespace Orbis.Stream.Tests;

public sealed class StreamingPlatformPageTests : IClassFixture<ApplicationFixture>
{
    private readonly ApplicationFixture _fixture;

    public StreamingPlatformPageTests(ApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_channel_settings_show_the_personal_ingest_help()
    {
        var html = await _fixture.Client.GetStringAsync("/orbis/mainChannelSetting?edit=new");

        Assert.Contains("data-show-when=\"kick facebook\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"facebook\" data-stream-url=\"rtmps://live-api-s.facebook.com:443/rtmp\"", html, StringComparison.Ordinal);
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
            Assert.Contains("<label class=\"choice twitch\">", wizard, StringComparison.Ordinal);

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
    public async Task The_wizard_lays_the_settings_out_in_columns_with_the_mark_of_their_platform()
    {
        var html = WebUtility.HtmlDecode(await _fixture.Client.GetStringAsync("/orbis/mainLive?start=1"));

        Assert.Contains("class=\"dialog wizard\" id=\"start-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"dialog wizard\" id=\"playlist-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"choice-list grid section\"", html, StringComparison.Ordinal);

        // The seeded settings of Twitch carry its colour and mark.
        Assert.Contains("<label class=\"choice twitch\">", html, StringComparison.Ordinal);
        Assert.Contains(LiveLinkView.MarkupOf("twitch"), html, StringComparison.Ordinal);
        Assert.Contains("1920×1080 · 30 fps · 6 Mbit/s", html, StringComparison.Ordinal);
    }
}
