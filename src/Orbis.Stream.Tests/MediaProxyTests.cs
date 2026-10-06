using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>The light copies of the heavy files of a canvas: which files get one, and how.</summary>
public sealed class MediaProxyTests
{
    private static MediaProbeResult Media(int width, int height, string pixelFormat = "yuv420p", string? transfer = null) =>
        new(width, height, 30, true, 2, 60, pixelFormat, transfer);

    [Theory]
    [InlineData(3840, 2160, "yuv420p10le", "smpte2084", true)]
    [InlineData(2560, 1440, "yuv420p", null, true)]
    [InlineData(2160, 3840, "yuv420p", null, true)]
    [InlineData(1920, 1200, "yuv420p", null, true)]
    // Full HD or less keeps its own picture, whatever it is encoded with.
    [InlineData(1920, 1080, "yuv420p10le", "smpte2084", false)]
    [InlineData(1080, 1920, "yuv420p", null, false)]
    [InlineData(1280, 720, "yuvj420p", null, false)]
    public void OnlyAFileBiggerThanFullHdGetsACopy(int width, int height, string pixelFormat, string? transfer, bool expected) =>
        Assert.Equal(expected, MediaProxyService.Needs(Media(width, height, pixelFormat, transfer)));

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(3840, 1600, 1920, 800)]
    [InlineData(2160, 3840, 1080, 1920)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1280, 720)]
    public void ACopyFitsTheBoxInTheShapeOfItsFileAndIsNeverBigger(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), MediaProxyService.SizeOf(width, height));

    [Fact]
    public void TheProbeReadsThePixelFormatAndTheTransfer()
    {
        var probe = new FfmpegProbe(new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger<FfmpegProbe>.Instance);
        var media = probe.Parse(
            """
            {"streams":[{"codec_type":"video","width":3840,"height":2160,"avg_frame_rate":"60/1",
              "pix_fmt":"yuv420p10le","color_transfer":"smpte2084"},
              {"codec_type":"audio","channels":2}],
             "format":{"duration":"10800.5"}}
            """,
            "rain.mp4");

        Assert.Equal("yuv420p10le", media.PixelFormat);
        Assert.Equal("smpte2084", media.ColorTransfer);
        Assert.True(MediaProxyService.IsHdr(media));
    }

    [Fact]
    public void TheCopyIsScaledFirstAndToneMappedOnlyWhenHdr()
    {
        var sdr = string.Join(' ', FfmpegCommandBuilder.BuildProxy("/in.mp4", "/out.mp4.part", 1920, 1080, toneMap: false));
        var hdr = string.Join(' ', FfmpegCommandBuilder.BuildProxy("/in.mp4", "/out.mp4.part", 1920, 1080, toneMap: true));

        Assert.Contains(
            "-vf scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p",
            sdr, StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap", sdr, StringComparison.Ordinal);
        Assert.Contains("force_divisible_by=2,zscale=t=linear", hdr, StringComparison.Ordinal);
        Assert.Contains("tonemap=tonemap=hable", hdr, StringComparison.Ordinal);
        Assert.Contains("-color_trc bt709", hdr, StringComparison.Ordinal);

        // The sound is optional, and the half made copy is never mistaken for the container it is.
        Assert.Contains("-map 0:v:0 -map 0:a:0?", sdr, StringComparison.Ordinal);
        Assert.EndsWith("-f mp4 /out.mp4.part", sdr, StringComparison.Ordinal);
        Assert.DoesNotContain("-hwaccel", sdr, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264 -preset veryfast -crf 20", sdr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGpuDecodesAndEncodesTheCopyWhenItWasTheFasterWay()
    {
        var gpu = string.Join(' ', FfmpegCommandBuilder.BuildProxy(
            "/in.mp4", "/out.mp4.part", 1920, 1080, toneMap: false, new ProxyEncoding(true, "h264_nvenc")));

        Assert.Contains("-hwaccel auto -i /in.mp4", gpu, StringComparison.Ordinal);
        Assert.Contains("-c:v h264_nvenc -preset p4 -rc vbr -cq 21 -b:v 0", gpu, StringComparison.Ordinal);
        Assert.DoesNotContain("libx264", gpu, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrialTimesTheFirstSecondsIntoNothing()
    {
        var trial = string.Join(' ', FfmpegCommandBuilder.BuildProxy(
            "/in.mp4", "-", 1920, 1080, toneMap: true, ProxyEncoding.Cpu, TimeSpan.FromSeconds(3)));

        Assert.EndsWith("-t 3 -f null -", trial, StringComparison.Ordinal);
        Assert.Contains("tonemap", trial, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveDecodesAFileOnTheGpuOnlyWhenItWasMeasuredFaster()
    {
        FfmpegCompositionItem[] items =
        [
            new(Orbis.Stream.Core.Domain.SourceKind.File, "/videos/4k.mp4", 0, 0, 960, 540, true, HardwareDecoding: true),
            new(Orbis.Stream.Core.Domain.SourceKind.File, "/videos/hd.mp4", 960, 0, 960, 540, true)
        ];
        var text = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            items, "rtmp://ingest/live/key", new Orbis.Stream.Core.Domain.VideoSettingEntity { Title = "t", VideoCodecName = "libx264" },
            1920, 540, 30)));

        Assert.Contains("-thread_queue_size 512 -hwaccel auto -i /videos/4k.mp4", text, StringComparison.Ordinal);
        Assert.Contains("-thread_queue_size 512 -i /videos/hd.mp4", text, StringComparison.Ordinal);
    }
}

/// <summary>A real copy of a heavy file, with a real ffmpeg. Skipped when ffmpeg is not installed.</summary>
public sealed class MediaProxyFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaProxyFfmpegTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AHeavyFileLaidOnACanvasGetsALightCopyTheLiveThenStreams()
    {
        var ffmpeg = Which("ffmpeg");
        var ffprobe = Which("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the proxy test is skipped.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("orbis-proxy-");
        try
        {
            // Bigger than the box, 10 bit, tagged PQ: everything that makes a file heavy.
            var heavy = Path.Combine(directory.FullName, "heavy.mp4");
            var light = Path.Combine(directory.FullName, "light.mp4");
            await RunAsync(ffmpeg,
                $"-y -f lavfi -i testsrc2=size=2560x1440:rate=30 -f lavfi -i sine=frequency=440 -t 2 "
                + "-c:v libx265 -preset ultrafast -pix_fmt yuv420p10le -x265-params log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc "
                + $"-c:a aac -shortest \"{heavy}\"");
            await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=size=1280x720:rate=30 -t 2 -pix_fmt yuv420p \"{light}\"");

            var locator = new FfmpegToolLocator(ffmpeg, ffprobe);
            var probe = new FfmpegProbe(locator, NullLogger<FfmpegProbe>.Instance);
            var proxies = Path.Combine(directory.FullName, "proxies");
            using var service = new MediaProxyService(proxies, locator, probe, NullLogger<MediaProxyService>.Instance);

            var heavyMedia = await probe.ProbeAsync(heavy, CancellationToken.None);
            var lightMedia = await probe.ProbeAsync(light, CancellationToken.None);

            // Before the copy is made the live streams the file as it is.
            Assert.Equal(heavy, service.Resolve(heavy, heavyMedia, 960, 540).Path);

            service.Prepare(heavy);
            service.Prepare(light);

            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            string? copy = null;
            while (copy is null && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(100);
                var input = service.Resolve(heavy, heavyMedia, 960, 540);
                copy = input.Path == heavy ? null : input.Path;
            }

            Assert.NotNull(copy);
            var copyMedia = await probe.ProbeAsync(copy!, CancellationToken.None);
            Assert.Equal(1920, copyMedia.Width);
            Assert.Equal(1080, copyMedia.Height);
            Assert.Equal("yuv420p", copyMedia.PixelFormat);
            Assert.True(copyMedia.HasAudio, "the copy lost the sound");
            Assert.InRange(copyMedia.DurationSeconds, 1.8, 2.2);
            Assert.False(MediaProxyService.Needs(copyMedia));

            // A tile bigger than the copy is better served by the file, and a light file has no copy.
            Assert.Equal(heavy, service.Resolve(heavy, heavyMedia, 2560, 1440).Path);
            Assert.Equal(light, service.Resolve(light, lightMedia, 1280, 720).Path);
            Assert.Single(Directory.GetFiles(proxies));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string? Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);

    private static async Task RunAsync(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
