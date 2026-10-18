using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class SharedPublisherStateTests
{
    [Fact]
    public async Task BufferedFramesDoNotMakeABlockedPublishLive()
    {
        var ffmpeg = Tool("ffmpeg");
        var ffprobe = Tool("ffprobe");
        if (ffmpeg is null || ffprobe is null) return;
        await using var host = TestHostRunner.Start(ffmpeg, ffprobe);
        var clip = await ClipAsync(ffmpeg, host.DataDirectory);
        var videos = host.Services.GetRequiredService<VideoRepository>();
        var video = videos.Insert(new VideoEntity
        {
            Name = "clip.mp4", VideoPath = clip, Extension = "mp4", ChannelName = "publisher-test",
            VideoSettingId = 1, LiveStatus = LiveStatus.Connecting
        });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"rtmp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live2/test-key-1234";
        var profile = StreamPlatformProfile.YouTube with { ConnectTimeout = TimeSpan.FromSeconds(5) };
        var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
        await using var output = LiveOutput.For(url, locator, NullLogger.Instance, profile: profile)!;
        var frames = host.Services.GetRequiredService<LivePreviewFrames>();
        await using var session = FfmpegStreamingSession.Start(
            locator, video, clip, url, Setting(), new MediaProbeResult(320, 240, 30, true, 2, 30),
            NullLogger.Instance, previewPath: frames.PathOf(video), output: output);
        host.Services.GetRequiredService<StreamingSessionRegistry>().Register(session);

        // The socket accepts TCP but never answers the RTMP handshake. The encoder still
        // creates preview frames and the relay fills the sender's stdin.
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await UntilAsync(() => frames.Latest(video) is { Bytes.Length: > 0 });
        await Task.Delay(200);
        Assert.False(session.HasExited);
        Assert.False(session.IsOnAir);
        Assert.False(session.IsPublishing);
        Assert.Equal(0, session.PositionMilliseconds);
        var snapshot = host.Services.GetRequiredService<LivePreviewService>().Snapshot(video);
        Assert.False(snapshot.IsLive);
        Assert.Equal(LiveStatus.Connecting, snapshot.Status);
        Assert.Empty(snapshot.Running);

        peer.Close();
        await UntilAsync(() => session.HasExited);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, await session.WaitForExitAsync(timeout.Token));
        var error = await session.ReadErrorAsync();
        Assert.Contains("rtmp", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-key-1234", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewTracksTheSenderUntilItExits()
    {
        var ffmpeg = Tool("ffmpeg");
        var ffprobe = Tool("ffprobe");
        if (ffmpeg is null || ffprobe is null) return;
        await using var host = TestHostRunner.Start(ffmpeg, ffprobe);
        var clip = await ClipAsync(ffmpeg, host.DataDirectory);
        var videos = host.Services.GetRequiredService<VideoRepository>();
        var video = videos.Insert(new VideoEntity
        {
            Name = "clip.mp4", VideoPath = clip, Extension = "mp4", ChannelName = "publisher-test",
            VideoSettingId = 1, LiveStatus = LiveStatus.Connecting
        });
        var url = $"rtmp://127.0.0.1:{RtmpPublisherUrlTests.FreePort()}/live2/test-key-1234";
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-listen", "1", "-timeout", "15", "-i", url,
                     "-c", "copy", "-f", "null", "-" }) startInfo.ArgumentList.Add(argument);
        using var server = Process.Start(startInfo)!;
        var errors = server.StandardError.ReadToEndAsync();
        var serverOutput = server.StandardOutput.ReadToEndAsync();
        try
        {
            await Task.Delay(300);
            var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
            await using var output = LiveOutput.For(url, locator, NullLogger.Instance, profile: StreamPlatformProfile.YouTube)!;
            await using var session = FfmpegStreamingSession.Start(
                locator, video, clip, url, Setting(), new MediaProbeResult(320, 240, 30, true, 2, 30),
                NullLogger.Instance, output: output);
            var registry = host.Services.GetRequiredService<StreamingSessionRegistry>();
            registry.Register(session);
            var preview = host.Services.GetRequiredService<LivePreviewService>();
            await UntilAsync(() => session.IsPublishing);
            Assert.True(session.IsOnAir);
            Assert.True(preview.Snapshot(video).IsLive);
            Assert.Single(preview.Snapshot(video).Running);
            Assert.True(session.PositionMilliseconds > 0);
            // An explicitly watched live must not silently change to another channel.
            Assert.False(preview.Snapshot(video + 1).IsLive);

            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await UntilAsync(() => session.HasExited);
            Assert.True(session.IsOnAir); // Retain the history for the reconnection budget.
            Assert.False(session.IsPublishing);
            Assert.False(preview.Snapshot(video).IsLive);
            Assert.Empty(preview.Snapshot(video).Running);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await errors;
            await serverOutput;
        }
    }

    private static VideoSettingEntity Setting() => new()
    {
        Title = "publisher", VideoCodec = 27, VideoCodecName = "libx264", PixelFormat = 0,
        VideoBitrate = 800_000, VideoFormat = "flv", GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    private static async Task<string> ClipAsync(string ffmpeg, string directory)
    {
        var clip = Path.Combine(directory, "clip.mp4");
        using var process = Process.Start(new ProcessStartInfo(ffmpeg,
            $"-y -f lavfi -i testsrc2=size=320x240:rate=30 -f lavfi -i sine=frequency=440:sample_rate=44100 -t 30 -c:v libx264 -preset ultrafast -c:a aac \"{clip}\"")
        { RedirectStandardError = true, UseShellExecute = false })!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return clip;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition(), "The expected publisher state was not reached within 20 seconds.");
    }

    private static string? Tool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator).Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
}
