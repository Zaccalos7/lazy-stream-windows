using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Container/stream information of an input file, as returned by ffprobe.</summary>
public sealed record MediaProbeResult(
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio,
    int AudioChannels,
    double DurationSeconds);

/// <summary>
/// What an encoder is asked to produce: the source as it is, unless the setting overrides the
/// resolution or the frame rate. The transcode is built on it and the preview shows it next to
/// the source, so the two are computed in one place only.
/// </summary>
public sealed record MediaOutput(int Width, int Height, double FrameRate);

/// <summary>Port of the <c>FFmpegFrameGrabber</c> probing performed before every stream.</summary>
public sealed class FfmpegProbe
{
    private readonly FfmpegToolLocator _locator;
    private readonly ILogger<FfmpegProbe> _logger;

    public FfmpegProbe(FfmpegToolLocator locator, ILogger<FfmpegProbe> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    public async Task<MediaProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        string[] arguments = ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", inputPath];

        var output = await _locator
            .RunCapturedAsync(_locator.FfprobePath, arguments, cancellationToken)
            .ConfigureAwait(false);

        return Parse(output, inputPath);
    }

    public MediaProbeResult Parse(string ffprobeJson, string inputPath)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(ffprobeJson) ? "{}" : ffprobeJson);
        var root = document.RootElement;

        var width = 0;
        var height = 0;
        var frameRate = 0d;
        var hasAudio = false;
        var audioChannels = 0;
        var duration = 0d;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var codecTypeValue)
                    ? codecTypeValue.GetString()
                    : null;

                if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                {
                    width = stream.TryGetProperty("width", out var widthValue) ? widthValue.GetInt32() : width;
                    height = stream.TryGetProperty("height", out var heightValue) ? heightValue.GetInt32() : height;
                    // avg_frame_rate is the one JavaCV exposed, but it can be 0/0 for variable
                    // frame rate files: fall back to the nominal rate in that case.
                    var average = stream.TryGetProperty("avg_frame_rate", out var frameRateValue)
                        ? ParseRatio(frameRateValue.GetString())
                        : 0;
                    var nominal = stream.TryGetProperty("r_frame_rate", out var rFrameRateValue)
                        ? ParseRatio(rFrameRateValue.GetString())
                        : 0;
                    frameRate = average > 0 ? average : nominal;
                }
                else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                    audioChannels = stream.TryGetProperty("channels", out var channelsValue) ? channelsValue.GetInt32() : audioChannels;
                }
            }
        }

        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationValue)
            && double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
        {
            duration = parsedDuration;
        }

        if (width <= 0 || height <= 0)
        {
            _logger.LogWarning(
                "No video stream detected in {Input} (width={Width}, height={Height})", inputPath, width, height);
        }

        return new MediaProbeResult(width, height, frameRate <= 0 ? 25 : frameRate, hasAudio, audioChannels, duration);
    }

    private static double ParseRatio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }
}

/// <summary>Resolves ffmpeg/ffprobe, mirroring the FFmpegFrameGrabber/Recorder of the Java version
/// (which used the JavaCV bundled binaries) through the executables configured for the app.</summary>
public sealed class FfmpegToolLocator
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public FfmpegToolLocator(string ffmpegPath, string ffprobePath)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    public string FfmpegPath => _ffmpegPath;

    public string FfprobePath => _ffprobePath;

    /// <summary>
    /// One <see cref="ProcessStartInfo.ArgumentList"/> entry per argument: .NET escapes each one for
    /// the OS, so paths with spaces, quotes or a trailing backslash reach ffmpeg unchanged.
    /// </summary>
    public ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        return startInfo;
    }

    public async Task<string> RunCapturedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments) };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
