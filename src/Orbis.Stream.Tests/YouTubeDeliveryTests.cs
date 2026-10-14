using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class YouTubeDeliveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    public async Task YouTubeSenderPublishesDecodableAudioAndVideoAcrossEncoderChanges(bool sourceHasAudio, bool composition, int resumeSeconds)
    {
        var ffmpeg = Tool("ffmpeg");
        var ffprobe = Tool("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            output.WriteLine("ffmpeg/ffprobe unavailable: YouTube delivery integration requires both tools.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("orbis-youtube-delivery-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
        var logger = new OutputLogger(output);
        try
        {
            var clip = Path.Combine(directory.FullName, "source.mp4");
            List<string> generate = ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=25"];
            if (sourceHasAudio)
            {
                generate.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]);
            }
            generate.AddRange(["-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", clip]);
            await locator.RunCapturedAsync(ffmpeg, generate, token);
            var probe = await new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance).ProbeAsync(clip, token);
            var received = Path.Combine(directory.FullName, "received.flv");
            var url = $"rtmp://127.0.0.1:{RtmpPublisherUrlTests.FreePort()}/live2/local-test";
            var info = locator.CreateStartInfo(ffmpeg,
                ["-v", "error", "-listen", "1", "-timeout", "20", "-i", url, "-map", "0", "-c", "copy", "-y", received]);
            using var server = Process.Start(info)!;
            var serverErrors = server.StandardError.ReadToEndAsync();
            var serverOutput = server.StandardOutput.ReadToEndAsync();
            try
            {
                await Task.Delay(500, token);
                var setting = new VideoSettingEntity
                {
                    VideoCodec = 27, VideoCodecName = "libx264", PixelFormat = 0,
                    VideoWidth = 1280, VideoHeight = 720, FrameRate = 30,
                    VideoBitrate = 3_000_000, VideoFormat = "flv", GopSize = 2,
                    AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
                    VideoSettingsOptions = [new() { Key = VideoSettingLatency.OptionKey, Value = "0" }]
                };
                var overlay = Path.Combine(directory.FullName, "logo.png");
                await File.WriteAllBytesAsync(overlay,
                    TestPictures.Png(64, 32, (x, _) => x < 32 ? (255, 0, 0, 255) : (0, 0, 0, 0)), token);
                var preview = Path.Combine(directory.FullName, "preview.jpg");
                await using (var live = LiveOutput.For(url, locator, logger, profile: StreamPlatformProfile.YouTube)!)
                {
                    foreach (var quality in new[] { EncoderQuality.Balanced, EncoderQuality.Light, EncoderQuality.Balanced })
                    {
                        await using var session = composition
                            ? FfmpegStreamingSession.StartComposition(locator, 1,
                                [new(SourceKind.File, clip, 0, 0, 1280, 720, sourceHasAudio),
                                 new(SourceKind.Overlay, overlay, 960, 0, 320, 160, false, Overlay: OverlayMedia.Still)],
                                url, setting, 1280, 720, 30, logger,
                                resumeFrom: TimeSpan.FromSeconds(resumeSeconds),
                                duration: TimeSpan.FromSeconds(6), previewPath: preview, output: live, quality: quality)
                            : FfmpegStreamingSession.Start(locator, 1, clip, url, setting, probe,
                                logger, previewPath: preview, output: live, quality: quality);
                        var ended = session.WaitForExitAsync(token);
                        var previewSeen = false;
                        // A finite scene must flush its video encoder and finish on its own,
                        // even with a preview. Killing it would hide an infinite canvas and
                        // could discard the delayed frames at the end of each pass.
                        while (!ended.IsCompleted)
                        {
                            previewSeen |= File.Exists(preview) && new FileInfo(preview).Length > 0;
                            await Task.Delay(20, token);
                        }
                        var exit = await ended;
                        Assert.True(exit == 0, await session.ReadErrorAsync());
                        Assert.True(previewSeen, "The encoder produced no JPEG preview while publishing.");
                    }
                    await live.CompleteAsync(token);
                    output.WriteLine("Published timeline: " + live.PublishedMilliseconds + " ms");
                }
                await server.WaitForExitAsync(token);
                Assert.True(server.ExitCode == 0, await serverErrors);
                await serverOutput;

                // The ingest accepting bytes and ffprobe listing a video stream are insufficient:
                // decode every received audio/video packet, including both encoder transitions.
                await locator.RunCapturedAsync(ffmpeg,
                    ["-v", "error", "-xerror", "-i", received, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], token);
                var json = await locator.RunCapturedAsync(ffprobe,
                    ["-v", "error", "-count_frames", "-show_entries", "stream=codec_name,codec_type,width,height,sample_rate,channels,nb_read_frames", "-of", "json", received], token);
                using var parsed = JsonDocument.Parse(json);
                var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var video = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "video");
                Assert.Equal("h264", video.GetProperty("codec_name").GetString());
                Assert.Equal(1280, video.GetProperty("width").GetInt32());
                Assert.Equal(720, video.GetProperty("height").GetInt32());
                var frames = int.Parse(video.GetProperty("nb_read_frames").GetString()!);
                // Every canvas frame, including the delayed frames flushed at each handover,
                // must survive. A resumed canvas contains only the part after its seek point.
                if (composition)
                {
                    Assert.Equal((6 - resumeSeconds) * 30 * 3, frames);
                }
                else
                {
                    Assert.InRange(frames, 525, 540);
                }
                var audio = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
                Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
                Assert.Equal("48000", audio.GetProperty("sample_rate").GetString());
                Assert.Equal(2, audio.GetProperty("channels").GetInt32());
            }
            finally
            {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
                await Task.WhenAll(serverErrors, serverOutput);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string? Tool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name))
        .FirstOrDefault(File.Exists);

    private sealed class OutputLogger(ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            output.WriteLine(formatter(state, exception));
    }
}
