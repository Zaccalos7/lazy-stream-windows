using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class FfmpegProcessLogTests
{
    private const string Secret = "private-stream-key-123456789";
    private const string Url = "rtmps://a.rtmps.youtube.com/live2/" + Secret;

    [Fact]
    public async Task AWarningIsLoggedBeforeTheRunningProcessClosesItsPipe()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var logger = new CapturingLogger();
        var reading = FfmpegProcessLog.ReadAsync(reader, logger, "YouTube", "sender", Url);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"[warning] Video timestamps on {Url}\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (logger.Entries.IsEmpty)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.False(reading.IsCompleted);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("****", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Text, StringComparison.Ordinal);
        await pipe.Writer.CompleteAsync();
        var saved = await reading.WaitAsync(timeout.Token);
        Assert.DoesNotContain(Secret, saved, StringComparison.Ordinal);
        Assert.Contains("Video timestamps", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsRetainABoundedTailAndTheirSeverity()
    {
        var text = "[info] " + new string('x', 30_000) + $" {Secret}\n[error] failed to encode\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var logger = new CapturingLogger();
        var tail = await FfmpegProcessLog.ReadAsync(reader, logger, "YouTube", "encoder", Url);
        Assert.InRange(tail.Length, 1, 16_384);
        Assert.EndsWith("[error] failed to encode", tail, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, tail, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(logger.Entries, entry =>
        {
            Assert.True(entry.Text.Length < 4200);
            Assert.DoesNotContain(Secret, entry.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SenderKeepsConnectionDebugButDropsPacketDebugAfterOpeningOutput()
    {
        const string url = "rtmps://live-api-s.facebook.com:443/rtmp/FB-private/secret?token=long-private-token";
        const string lines = "[debug] Sending publish command for 'FB-private/secret'\n"
            + "[info] Output #0, flv\n[debug] packet details\n[error] connection lost\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(lines)));
        var logger = new CapturingLogger();
        var tail = await FfmpegProcessLog.ReadAsync(reader, logger, "Facebook", "sender", url);
        Assert.Contains("Sending publish command for '****'", tail, StringComparison.Ordinal);
        Assert.Contains("connection lost", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("packet details", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("FB-private", tail, StringComparison.Ordinal);
        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void YouTubeEncoderAndSenderExposeCodecInformationAndWarnings()
    {
        var sender = FfmpegCommandBuilder.BuildSender(Url, StreamPlatformProfile.YouTube);
        Assert.Equal("level+info", sender[sender.ToList().IndexOf("-loglevel") + 1]);
        Assert.Equal("4194304", sender[sender.ToList().IndexOf("-probesize") + 1]);
        Assert.Equal("2000000", sender[sender.ToList().IndexOf("-analyzeduration") + 1]);
        var setting = new VideoSettingEntity { VideoCodecName = "libx264" };
        var encoder = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "clip.mp4", Url, new(1280, 720, 30, true, 2, 60), setting, Profile: StreamPlatformProfile.YouTube));
        Assert.Equal("level+info", encoder[encoder.ToList().IndexOf("-loglevel") + 1]);
        var twitch = FfmpegCommandBuilder.BuildSender("rtmp://live.twitch.tv/app/key", StreamPlatformProfile.Twitch);
        Assert.Equal("error", twitch[twitch.ToList().IndexOf("-loglevel") + 1]);
    }

    [Fact]
    public void FacebookGamingIsGivenTheSameCodecProbeAsYouTube()
    {
        // The delivery is YouTube's, and so is the reason for the probe: Facebook reads the header
        // of the publish and keeps a broadcast it cannot decode off air. A small probe leaves the
        // header without the parameters of one of the two codecs (see StreamPlatformProfile).
        var facebook = FfmpegCommandBuilder.BuildSender(Url, StreamPlatformProfile.Facebook);
        Assert.True(StreamPlatformProfile.Facebook.CompleteHeaders);
        Assert.Equal("4194304", facebook[facebook.ToList().IndexOf("-probesize") + 1]);
        Assert.Equal("2000000", facebook[facebook.ToList().IndexOf("-analyzeduration") + 1]);

        // A platform that reads the tags as they come still gets the small probe.
        var twitch = FfmpegCommandBuilder.BuildSender("rtmp://live.twitch.tv/app/key", StreamPlatformProfile.Twitch);
        Assert.False(StreamPlatformProfile.Twitch.CompleteHeaders);
        Assert.Equal("65536", twitch[twitch.ToList().IndexOf("-probesize") + 1]);
        Assert.Equal("500000", twitch[twitch.ToList().IndexOf("-analyzeduration") + 1]);
    }

    [Fact]
    public void FacebookGamingReportsItsEncoderAndSenderLikeYouTube()
    {
        // The same delivery, the same need to see what ffmpeg makes of the stream: a header
        // Facebook cannot read is a warning, and a warning is hidden below the error level.
        Assert.True(StreamPlatformProfile.Facebook.Diagnostics);
        var sender = FfmpegCommandBuilder.BuildSender(Url, StreamPlatformProfile.Facebook);
        Assert.Equal("level+debug", sender[sender.ToList().IndexOf("-loglevel") + 1]);

        var setting = new VideoSettingEntity { VideoCodecName = "libx264" };
        var encoder = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "clip.mp4", Url, new(1280, 720, 30, true, 2, 60), setting, Profile: StreamPlatformProfile.Facebook));
        Assert.Equal("level+info", encoder[encoder.ToList().IndexOf("-loglevel") + 1]);

        // And the lines carry the platform they belong to, not YouTube's name by default.
        Assert.Equal("Facebook Gaming", StreamPlatformProfile.Facebook.Name);
        Assert.Equal("YouTube", StreamPlatformProfile.YouTube.Name);

        // A platform that is not reported stays at the error level.
        Assert.False(StreamPlatformProfile.Twitch.Diagnostics);
        var twitch = FfmpegCommandBuilder.BuildSender("rtmp://live.twitch.tv/app/key", StreamPlatformProfile.Twitch);
        Assert.Equal("error", twitch[twitch.ToList().IndexOf("-loglevel") + 1]);
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
