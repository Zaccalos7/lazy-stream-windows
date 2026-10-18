using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class AnimatedWebpTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Ffmpeg9DecodesAndLoopsAnimatedWebpWithTransparencyOnTheCanvas()
    {
        var locator = new FfmpegToolLocator("ffmpeg", "ffprobe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        string demuxers;
        try
        {
            demuxers = await locator.RunCapturedAsync(locator.FfmpegPath, ["-hide_banner", "-demuxers"], token);
        }
        catch (Win32Exception)
        {
            output.WriteLine("FFmpeg is unavailable: animated WebP integration requires FFmpeg 9.");
            return;
        }
        if (!demuxers.Contains("webp_anim", StringComparison.Ordinal))
        {
            output.WriteLine("This FFmpeg has no webp_anim demuxer: run with FFmpeg 9 on PATH.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "orbis-webp9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var frame = 0; frame < 2; frame++)
            {
                var green = frame == 1;
                await File.WriteAllBytesAsync(Path.Combine(directory, $"frame-{frame}.png"),
                    TestPictures.Png(64, 32, (x, _) => x >= 32 ? (0, 0, 0, 0) : green ? (0, 255, 0, 255) : (255, 0, 0, 255)), token);
            }
            var webp = Path.Combine(directory, "badge.webp");
            await locator.RunCapturedAsync(locator.FfmpegPath,
                ["-v", "error", "-framerate", "2", "-i", Path.Combine(directory, "frame-%d.png"),
                 "-frames:v", "2", "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "1", webp], token);

            var probe = await new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance).ProbeAsync(webp, token);
            Assert.Equal((64, 32, "webp_anim", "webp_anim"), (probe.Width, probe.Height, probe.Format, probe.VideoCodec));
            var media = OverlayMedia.Of(probe);
            Assert.True(media.Loop);

            var background = Path.Combine(directory, "background.mkv");
            await locator.RunCapturedAsync(locator.FfmpegPath,
                ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x32:r=8", "-t", "3", "-c:v", "ffv1", background], token);
            var pixels = Path.Combine(directory, "canvas.rgb");
            var setting = new VideoSettingEntity
            {
                VideoCodecName = "rawvideo", VideoFormat = "rawvideo", PixelFormat = 2,
                VideoSettingsOptions = [new VideoSettingsOptionEntity { Key = "threads", Value = "1" }]
            };
            var original = await File.ReadAllBytesAsync(webp, token);
            await using (var session = FfmpegStreamingSession.StartComposition(locator, 1,
                [new(SourceKind.File, background, 0, 0, 64, 32, false),
                 new(SourceKind.Overlay, webp, 0, 0, 64, 32, false, Overlay: media)],
                pixels, setting, 64, 32, 8, NullLogger<FfmpegStreamingSession>.Instance, duration: TimeSpan.FromSeconds(3)))
            {
                var exit = await session.WaitForExitAsync(token);
                Assert.True(exit == 0, await session.ReadErrorAsync());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(webp, token));
            var rgb = await File.ReadAllBytesAsync(pixels, token);
            Assert.Equal(24 * 64 * 32 * 3, rgb.Length);
            // Both colors must recur after the one-second animation ends. Its transparent half
            // must keep revealing the blue source throughout all three loops.
            foreach (var frame in new[] { 1, 9, 17 })
            {
                var offset = (frame * 64 * 32 + 16 * 64 + 16) * 3;
                Assert.True(rgb[offset] > 200 && rgb[offset + 1] < 30, $"Frame {frame}: red badge did not loop.");
            }
            foreach (var frame in new[] { 5, 13, 21 })
            {
                var offset = (frame * 64 * 32 + 16 * 64 + 16) * 3;
                Assert.True(rgb[offset + 1] > 200 && rgb[offset] < 30, $"Frame {frame}: green badge did not animate.");
                offset = (frame * 64 * 32 + 16 * 64 + 48) * 3;
                Assert.True(rgb[offset + 2] > 200 && rgb[offset] < 30, $"Frame {frame}: WebP lost transparency.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
