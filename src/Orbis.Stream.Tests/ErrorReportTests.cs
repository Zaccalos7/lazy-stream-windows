using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Tests;

public sealed class ErrorReportTests : IClassFixture<ApplicationFixture>
{
    private readonly ApplicationFixture _fixture;
    public ErrorReportTests(ApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void LongUnicodeErrorsRemainCompleteAcrossQrPages()
    {
        var error = string.Concat(Enumerable.Repeat("Errore 日本語 🎥\n", 2000));
        var parts = ErrorReport.Parts(error);
        Assert.True(parts.Count > 1);
        Assert.Equal(error, string.Concat(parts));
        Assert.All(parts, part =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(part), 1, 600);
            Assert.DoesNotContain("\uFFFD", part, StringComparison.Ordinal);
        });
        var image = ErrorReport.Image(parts[0], 0, parts.Count);
        var png = Convert.FromBase64String(image.Split(',')[1]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
    }

    [Fact]
    public async Task FailedLiveHasReportButtonsAndALocalQrReportWithoutItsStreamKey()
    {
        var histories = _fixture.Services.GetRequiredService<VideoLiveHistoryRepository>();
        var videos = _fixture.Services.GetRequiredService<VideoRepository>();
        const string key = "private-key-123456?token=secret-value";
        var historyId = histories.Insert(new VideoLiveHistoryEntity
        {
            StreamUrl = "rtmps://live-api-s.facebook.com:443/rtmp",
            StreamKey = key, PlatformStreamName = "facebook", UserName = "qr-report-test",
            FolderOfVideoToStream = "/clips/qr-report", LocalDateTimeStartLive = DateTime.Now
        });
        var id = videos.Insert(new VideoEntity
        {
            Name = "qr-report.mp4", VideoPath = "/clips/qr-report.mp4", Extension = "mp4",
            ChannelName = "qr-report-test", VideoSettingId = 1, VideoLiveHistoryId = historyId,
            LiveStatus = LiveStatus.Error, StartDateLive = DateTime.Now,
            Message = $"publish {key} failed\nrtmps://ingest/rtmp/{key}\nprivate-key-123456 token secret-value\n<script>alert(1)</script>"
        });
        try
        {
            foreach (var page in new[] { "/orbis/mainLiveHistory", "/orbis/mainLive" })
            {
                var html = await _fixture.Client.GetStringAsync(page + "?channelName=qr-report-test");
                Assert.Contains($"data-error-report=\"{id}\"", html, StringComparison.Ordinal);
                Assert.Contains("id=\"error-report-dialog\"", html, StringComparison.Ordinal);
            }
            using var response = await _fixture.Client.GetAsync($"/video/{id}/error-report");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var report = await response.Content.ReadFromJsonAsync<JsonElement>();
            var text = report.GetProperty("text").GetString()!;
            Assert.Contains("failed", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-key", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
            Assert.StartsWith("data:image/png;base64,", report.GetProperty("images")[0].GetString(), StringComparison.Ordinal);
            var video = videos.FindByPkid(id)!;
            video.LiveStatus = LiveStatus.Ended;
            videos.Update(video);
            using var noError = await _fixture.Client.GetAsync($"/video/{id}/error-report");
            Assert.Equal(HttpStatusCode.NotFound, noError.StatusCode);
            foreach (var page in new[] { "/orbis/mainLiveHistory", "/orbis/mainLive" })
            {
                var html = await _fixture.Client.GetStringAsync(page + "?channelName=qr-report-test");
                Assert.DoesNotContain($"data-error-report=\"{id}\"", html, StringComparison.Ordinal);
            }
        }
        finally
        {
            videos.Delete(id);
            histories.DeleteRow(historyId);
        }
    }
}
