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
        var reading = FfmpegProcessLog.ReadAsync(reader, logger, "sender", Url);
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
        var tail = await FfmpegProcessLog.ReadAsync(reader, logger, "encoder", Url);
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

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
