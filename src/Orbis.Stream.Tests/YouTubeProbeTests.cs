using System.Diagnostics;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class YouTubeProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TheSenderRecognizes48KhzAacAfterALargeInitialVideoKeyframe()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator).Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            .FirstOrDefault(File.Exists);
        if (ffmpeg is null) return;
        var directory = Directory.CreateTempSubdirectory("orbis-youtube-probe-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var locator = new FfmpegToolLocator(ffmpeg, "ffprobe");
        try
        {
            var fixture = Path.Combine(directory.FullName, "large-keyframe.flv");
            await locator.RunCapturedAsync(ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30,noise=alls=100:allf=t+u",
                 "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "libx264",
                 "-preset", "superfast", "-pix_fmt", "yuv420p", "-b:v", "6000000", "-maxrate", "6000000",
                 "-bufsize", "6000000", "-g", "60", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-flvflags", "no_duration_filesize", fixture], token);

            var old = await PublishAsync("old", smallProbe: true);
            var current = await PublishAsync("current", smallProbe: false);
            output.WriteLine("Old probe: " + AudioLine(old));
            output.WriteLine("Current probe: " + AudioLine(current));
            Assert.Contains("Audio: aac, 44100 Hz", old, StringComparison.Ordinal);
            Assert.Contains("Audio: aac (LC), 48000 Hz, stereo", current, StringComparison.Ordinal);
            Assert.DoesNotContain("Could not find codec parameters", current, StringComparison.Ordinal);

            async Task<string> PublishAsync(string name, bool smallProbe)
            {
                var arguments = FfmpegCommandBuilder.BuildSender(Path.Combine(directory.FullName, name + ".flv"),
                    StreamPlatformProfile.YouTube with { Pacing = RelayPacing.Relay }).ToList();
                if (smallProbe)
                {
                    arguments[arguments.IndexOf("-probesize") + 1] = "65536";
                    arguments[arguments.IndexOf("-analyzeduration") + 1] = "500000";
                }
                var info = locator.CreateStartInfo(ffmpeg, arguments);
                info.RedirectStandardInput = true;
                using var process = Process.Start(info)!;
                var error = process.StandardError.ReadToEndAsync();
                var progress = process.StandardOutput.ReadToEndAsync();
                try
                {
                    await using (var input = File.OpenRead(fixture))
                    {
                        await input.CopyToAsync(process.StandardInput.BaseStream, token);
                    }
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(token);
                    Assert.True(process.ExitCode == 0, await error);
                    await progress;
                    return await error;
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(error, progress);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string AudioLine(string log) =>
        log.Split('\n').FirstOrDefault(line => line.Contains("Audio:", StringComparison.Ordinal)) ?? "No audio parameters";
}
