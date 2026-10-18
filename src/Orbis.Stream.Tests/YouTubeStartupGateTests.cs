using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class YouTubeStartupGateTests
{
    [Fact]
    public void AnAttemptUpdatePreservesAConcurrentStopRequestAndTheResumePosition()
    {
        using var database = new TemporaryDatabase();
        var repository = database.Repository<VideoRepository>();
        var id = repository.Insert(new VideoEntity
        {
            Name = "program.mp4", VideoPath = "/program.mp4", Extension = "mp4", ChannelName = "channel",
            LiveStatus = LiveStatus.Offline, LastTimeStampBeforeStop = 42_000, VideoSettingId = 1
        });
        repository.SetStopFlag(id, true);
        repository.SetStartupState(id, LiveStatus.Reconnecting, "YouTube attempt 2/3", 42_000);
        var saved = repository.FindByPkid(id)!;
        Assert.True(saved.ShouldBeStop);
        Assert.Equal(42_000, saved.LastTimeStampBeforeStop);
        Assert.Equal(LiveStatus.Reconnecting, saved.LiveStatus);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public async Task ContentPositionStaysFrozenUntilYouTubeConfirms(int succeedsOn, bool unreadable)
    {
        var locator = Locator();
        if (locator is null) return;
        var directory = Directory.CreateTempSubdirectory("orbis-youtube-startup-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var url = $"rtmp://127.0.0.1:{RtmpPublisherUrlTests.FreePort()}/live2/startup-test";
        var files = new ConcurrentQueue<string>();
        var receiver = ReceiveAsync(locator, url, directory.FullName, files, receiving.Token);
        var registry = new StreamingSessionRegistry();
        var requests = 0;
        var embeds = new LivePlatformEmbeds(new HttpClient(new Replies(request =>
        {
            Assert.True(registry.TryGet(71, out var standby));
            Assert.True(standby!.IsStandby);
            Assert.Equal(2000, standby.PositionMilliseconds);
            Assert.Equal(2000, standby.ContinuationMilliseconds);
            if (request.RequestUri!.AbsolutePath.EndsWith("/streams") && request.RequestUri.Query.Length == 0)
                Interlocked.Increment(ref requests);
            if (unreadable) return "<html>consent</html>";
            return request.RequestUri.AbsolutePath.EndsWith("/streams")
                ? """{"contents":[{"contentId":"newVideo002","contentType":"LOCKUP_CONTENT_TYPE_VIDEO"}]}"""
                : requests >= succeedsOn
                    ? """{"videoDetails":{"videoId":"newVideo002","isLiveContent":true,"isLive":true}}"""
                    : """{"videoDetails":{"isLiveContent":true},"microformat":{"liveBroadcastDetails":{"isLiveNow":false}}}""";
        })), NullLogger<LivePlatformEmbeds>.Instance);
        var gate = Gate(embeds, registry, locator);
        var states = new List<LiveStatus>();
        try
        {
            await Task.Delay(500, timeout.Token);
            await using var output = LiveOutput.For(url, locator, NullLogger.Instance, profile: StreamPlatformProfile.YouTube)!;
            var result = await gate.WaitAsync(71, "@channel", output, Setting(), new MediaOutput(320, 180, 30),
                TimeSpan.FromSeconds(2), () => false, (status, _) => states.Add(status), timeout.Token);
            Assert.Equal(succeedsOn <= 3, result);
            Assert.Equal(result, output.BroadcastVerified);
            Assert.False(registry.TryGet(71, out _));
            Assert.Equal(Math.Min(succeedsOn, 3), requests);
            Assert.Equal(Math.Min(succeedsOn, 3), files.Count);
            if (!result) Assert.Equal(LiveStatus.Error, states[^1]);
            else
            {
                var source = Path.Combine(directory.FullName, "program.mp4");
                await locator.RunCapturedAsync(locator.FfmpegPath,
                    ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30",
                     "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "4",
                     "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", source], timeout.Token);
                var probe = await new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance).ProbeAsync(source, timeout.Token);
                await using var program = FfmpegStreamingSession.Start(locator, 71, source, url, Setting(), probe,
                    NullLogger.Instance, TimeSpan.FromSeconds(2), output: output, quality: EncoderQuality.Light);
                Assert.Equal(0, await program.WaitForExitAsync(timeout.Token));
                await output.CompleteAsync(timeout.Token);
                Assert.InRange(program.PositionMilliseconds, 3900, 4100);
            }
            receiving.Cancel();
            await receiver;
            foreach (var captured in files)
                await locator.RunCapturedAsync(locator.FfmpegPath,
                    ["-v", "error", "-xerror", "-i", captured, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], timeout.Token);
        }
        finally
        {
            receiving.Cancel();
            await receiver;
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopOrCancellationCannotStartAnotherAttempt(bool cancel)
    {
        var locator = Locator();
        if (locator is null) return;
        var registry = new StreamingSessionRegistry();
        var embeds = new LivePlatformEmbeds(new HttpClient(new Replies(_ => "<html>consent</html>")), NullLogger<LivePlatformEmbeds>.Instance);
        var gate = Gate(embeds, registry, locator);
        using var cancellation = new CancellationTokenSource();
        var stopped = false;
        var states = new List<LiveStatus>();
        await using var output = LiveOutput.For($"rtmp://127.0.0.1:{RtmpPublisherUrlTests.FreePort()}/live2/no-listener",
            locator, NullLogger.Instance, profile: StreamPlatformProfile.YouTube)!;
        var waiting = gate.WaitAsync(71, "channel", output, Setting(), new MediaOutput(320, 180, 30), TimeSpan.Zero,
            () => stopped, (status, _) => states.Add(status), cancellation.Token);
        await Task.Delay(300);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        else
        {
            stopped = true;
            Assert.False(await waiting);
        }
        Assert.Single(states, status => status == LiveStatus.Connecting);
        Assert.DoesNotContain(LiveStatus.Reconnecting, states);
        Assert.False(output.BroadcastVerified);
        Assert.False(registry.TryGet(71, out _));
        Assert.Equal(LiveStatus.Stopped, states[^1]);
    }

    private static YouTubeStartupGate Gate(LivePlatformEmbeds embeds, StreamingSessionRegistry registry, FfmpegToolLocator locator) =>
        new(embeds, registry, locator,
            new Localizer(new MessageCatalog(Path.Combine(AppContext.BaseDirectory, "Messages"), NullLogger<MessageCatalog>.Instance), new HttpContextAccessor()),
            NullLogger<YouTubeStartupGate>.Instance, TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));

    private static VideoSettingEntity Setting() => new()
    {
        VideoCodec = 27, VideoCodecName = "libx264", PixelFormat = 0, VideoWidth = 320, VideoHeight = 180,
        FrameRate = 30, VideoBitrate = 500_000, VideoFormat = "flv", GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
        VideoSettingsOptions = [new() { Key = VideoSettingLatency.OptionKey, Value = "0" }]
    };

    private static FfmpegToolLocator? Locator()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var ffmpeg = path.Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")).FirstOrDefault(File.Exists);
        var ffprobe = path.Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")).FirstOrDefault(File.Exists);
        return ffmpeg is null || ffprobe is null ? null : new FfmpegToolLocator(ffmpeg, ffprobe);
    }

    private static async Task ReceiveAsync(FfmpegToolLocator locator, string url, string directory,
        ConcurrentQueue<string> files, CancellationToken token)
    {
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var captured = Path.Combine(directory, $"attempt-{attempt}.flv");
                using var process = Process.Start(locator.CreateStartInfo(locator.FfmpegPath,
                    ["-v", "error", "-listen", "1", "-timeout", "30", "-i", url, "-map", "0", "-c", "copy", "-y", captured]))!;
                var errors = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                try
                {
                    while (!token.IsCancellationRequested && !process.HasExited)
                    {
                        if (File.Exists(captured) && !files.Contains(captured)) files.Enqueue(captured);
                        await Task.Delay(20, token);
                    }
                    await process.WaitForExitAsync(token);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(errors, stdout);
                }
                if (token.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class Replies(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request)) });
    }
}
