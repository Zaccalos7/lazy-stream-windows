using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Tests;

/// <summary>
/// The cleanup of the live history: the button that runs it, the one that saves its settings and
/// the row delete. Each of them used to answer an error the page then had to explain, so they are
/// exercised through the HTTP the page really uses.
/// </summary>
public sealed class LiveHistoryCleanupTests : IAsyncLifetime
{
    private TestHostRunner _host = null!;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task RunningTheCleanupAnswersHowManyRowsItDeleted()
    {
        var history = Repository<VideoLiveHistoryRepository>();
        var videos = Repository<VideoRepository>();

        var old = AddLive(history, videos, "old", DateTime.Now.AddMonths(-4));
        var older = AddLive(history, videos, "older", DateTime.Now.AddMonths(-7));
        var fresh = AddLive(history, videos, "fresh", DateTime.Now.AddDays(-2));

        using var response = await _host.Client.DeleteAsync("/video/live-history/older-than/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The envelope carries a message code, so the answer is the localized sentence and not the
        // "No message found under code ..." a built sentence used to produce.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("success", body.GetProperty("response").GetString());
        Assert.Equal("Auto cleanup completed: deleted 2 live history rows.", body.GetProperty("message").GetString());

        // The lives it worked on are gone with their videos, the recent one is untouched.
        Assert.Null(history.FindByPkid(old));
        Assert.Null(history.FindByPkid(older));
        Assert.Empty(videos.FindByLiveHistoryId(old));
        Assert.Empty(videos.FindByLiveHistoryId(older));
        Assert.NotNull(history.FindByPkid(fresh));
        Assert.Single(videos.FindByLiveHistoryId(fresh));
    }

    [Fact]
    public async Task DeletingALiveHistoryTakesItsVideosWithIt()
    {
        var history = Repository<VideoLiveHistoryRepository>();
        var videos = Repository<VideoRepository>();

        var deleted = AddLive(history, videos, "deleted", DateTime.Now.AddMonths(-3), videosPerLive: 2);
        var kept = AddLive(history, videos, "kept", DateTime.Now.AddMonths(-3));

        var html = await PostAsync("/orbis/mainLiveHistory?handler=Delete", new Dictionary<string, string>
        {
            ["id"] = deleted.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        Assert.Contains("class=\"infobar success\"", html, StringComparison.Ordinal);

        // The history row goes, and so do every video row that pointed at it: the foreign key has no
        // cascade, so leaving them behind would keep the row from being deleted at all.
        Assert.Null(history.FindByPkid(deleted));
        Assert.Empty(videos.FindByLiveHistoryId(deleted));

        // Another live is not collateral damage.
        Assert.NotNull(history.FindByPkid(kept));
        Assert.Single(videos.FindByLiveHistoryId(kept));
    }

    [Fact]
    public async Task SavingTheCleanupSettingsIsRememberedAndAnswersOnTheInfoBar()
    {
        await AddActiveConfiguration();
        var history = Repository<VideoLiveHistoryRepository>();
        var videos = Repository<VideoRepository>();
        var repository = Repository<SettingRepository>();

        var old = AddLive(history, videos, "saved-old", DateTime.Now.AddMonths(-9));

        var html = await PostAsync("/orbis/mainLiveHistory?handler=SaveAutoCleanup", new Dictionary<string, string>
        {
            ["enabled"] = "true",
            ["intervalMonths"] = "3",
            ["olderThanMonths"] = "6"
        });

        // The answer is the InfoBar at the top of the page, where the other pages put it, and not a
        // little box in the corner of it.
        Assert.Contains("class=\"infobar success\"", html, StringComparison.Ordinal);
        Assert.Contains("Setting updated successfully", html, StringComparison.Ordinal);
        Assert.DoesNotContain("toast-container", html, StringComparison.Ordinal);

        // The settings live on the active configuration, and only its cleanup fields moved.
        var active = repository.FindAll(new Dictionary<string, string>()).Single();
        var saved = repository.FindById(active.Id);
        Assert.True(saved!.AutoCleanupEnabled);
        Assert.Equal(3, saved.AutoCleanupIntervalMonths);
        Assert.Equal(6, saved.AutoCleanupOlderThanMonths);
        Assert.Equal(active.StreamUrl, saved.StreamUrl);
        Assert.Equal(active.ChannelName, saved.ChannelName);

        // Nothing was deleted: saving is not running.
        Assert.NotNull(history.FindByPkid(old));

        // Turning it off keeps the picks, so turning it back on finds the same period.
        await PostAsync("/orbis/mainLiveHistory?handler=SaveAutoCleanup", new Dictionary<string, string>
        {
            ["intervalMonths"] = "3",
            ["olderThanMonths"] = "6"
        });
        var disabled = repository.FindById(active.Id);
        Assert.False(disabled!.AutoCleanupEnabled);
        Assert.Equal(3, disabled.AutoCleanupIntervalMonths);
        Assert.Equal(6, disabled.AutoCleanupOlderThanMonths);
    }

    [Fact]
    public async Task RunningTheCleanupFromThePageDeletesWhatThePickSays()
    {
        await AddActiveConfiguration();
        var history = Repository<VideoLiveHistoryRepository>();
        var videos = Repository<VideoRepository>();
        var repository = Repository<SettingRepository>();

        var threeDaysOld = AddLive(history, videos, "run-three-days", DateTime.Now.AddDays(-3));
        var startedToday = AddLive(history, videos, "run-today", DateTime.Now.AddHours(-2));

        // The run button saves the picks on screen first, so "older than yesterday" has to delete
        // the three days old live and leave the one that started a couple of hours ago alone.
        var html = await PostAsync("/orbis/mainLiveHistory?handler=SaveAutoCleanup", new Dictionary<string, string>
        {
            ["enabled"] = "true",
            ["intervalMonths"] = "1",
            ["olderThanMonths"] = "0",
            ["runNow"] = "true"
        });

        Assert.Contains("Auto cleanup completed: deleted 1 live history rows.", html, StringComparison.Ordinal);

        Assert.Null(history.FindByPkid(threeDaysOld));
        Assert.Empty(videos.FindByLiveHistoryId(threeDaysOld));
        Assert.NotNull(history.FindByPkid(startedToday));

        var saved = repository.FindAll(new Dictionary<string, string>()).Single();
        Assert.True(saved.AutoCleanupEnabled);
        Assert.Equal(0, saved.AutoCleanupOlderThanMonths);
    }

    /// <summary>The cleanup follows the configuration that is streaming, which the tests need one of.</summary>
    private async Task AddActiveConfiguration()
    {
        using var saved = await _host.Client.PostAsJsonAsync("/settings/save", new
        {
            streamUrl = "rtmp://ingest/live",
            streamKey = "key",
            platformStreamName = "twitch",
            channelName = "channel-cleanup",
            isActive = true
        });

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
    }

    private T Repository<T>() where T : class => _host.Services.GetRequiredService<T>();

    /// <summary>A live history row with the video rows of a live that went through it.</summary>
    private static long AddLive(
        VideoLiveHistoryRepository history,
        VideoRepository videos,
        string name,
        DateTime start,
        int videosPerLive = 1)
    {
        var historyId = history.Insert(new VideoLiveHistoryEntity
        {
            FolderOfVideoToStream = "/clips",
            LocalDateTimeStartLive = start,
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "key",
            PlatformStreamName = "channel-cleanup",
            UserName = "orbis"
        });

        for (var index = 0; index < videosPerLive; index++)
        {
            videos.Insert(new VideoEntity
            {
                Name = $"{name}-{index}",
                VideoPath = $"/clips/{name}-{index}.mp4",
                Extension = "mp4",
                VideoLiveHistoryId = historyId,
                LiveStatus = LiveStatus.Ended,
                ShouldBeStop = false,
                StartDateLive = start,
                ChannelName = "channel-cleanup",
                VideoSettingId = 1
            });
        }

        return historyId;
    }

    /// <summary>
    /// Posts the form of the page and answers the page it redirects to: the antiforgery token is
    /// read off the page that draws the form, because the page handlers are protected by it.
    /// </summary>
    private async Task<string> PostAsync(string path, Dictionary<string, string> fields)
    {
        using var page = await _host.Client.GetAsync("/orbis/mainLiveHistory");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"");
        Assert.True(token.Success, "The live history page draws no antiforgery token.");

        fields["__RequestVerificationToken"] = token.Groups["token"].Value;
        using var posted = await _host.Client.PostAsync(path, new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        return await posted.Content.ReadAsStringAsync();
    }
}
