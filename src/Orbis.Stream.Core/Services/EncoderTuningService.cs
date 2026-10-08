using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Fits the encoder to the machine it runs on, which is what <see cref="EncoderQuality.Auto"/>
/// means.
/// <para>Whether a GPU is "powerful enough" for a level is not something its name says: NVENC,
/// QuickSync and AMF are fixed blocks of the chip, the same on a small card as on a big one of the
/// same generation, and what a level costs is time of that block per frame. So it is measured: a
/// few seconds of a synthetic picture at the size and rate of the live, encoded at the balanced
/// level. An encoder that runs that <see cref="RequiredSpeed"/> times faster than real time gets
/// it; one that does not gets the light level, which is the one that keeps the live fluid. The
/// answer is kept for the life of the application, so it is paid once per size of live.</para>
/// <para>The default settings are fitted the same way, once at startup: they encode on the GPU
/// when there is one that works, on the CPU otherwise, at the automatic level. The settings of the
/// user are theirs and are never changed here: the level they chose is the level they get.</para>
/// </summary>
public sealed class EncoderTuningService
{
    /// <summary>
    /// How much faster than real time an encoder has to be at a level for a live to get it. Twice is
    /// the headroom a live needs next to the encode: decoding the sources, composing a canvas, the
    /// preview, and the moments the machine is busy with something else.
    /// </summary>
    public const double RequiredSpeed = 2.0;

    /// <summary>The platforms of the settings this application seeds and owns.</summary>
    private static readonly string[] DefaultPlatforms = ["Twitch", "Youtube", "Kick", "Facebook Gaming", "TikTok"];

    /// <summary>The GPU encoders tried, in order; the first one that opens on this machine is used.</summary>
    private static readonly string[] HardwareEncoders = ["h264_nvenc", "h264_qsv", "h264_amf"];

    /// <summary>The options of a default setting that only x264 understands, or that a level replaces.</summary>
    private static readonly string[] EncoderSpecificOptions = ["preset", "tune", "x264-params"];

    private static readonly TimeSpan TrialLength = TimeSpan.FromSeconds(3);

    private readonly FfmpegToolLocator _locator;
    private readonly VideoSettingRepository _settings;
    private readonly ILogger<EncoderTuningService> _logger;
    private readonly Func<IReadOnlyList<string>, Task<bool>> _run;
    private readonly Lazy<Task<string?>> _hardwareEncoder;
    private readonly ConcurrentDictionary<string, Lazy<Task<EncoderQuality>>> _measured = new(StringComparer.Ordinal);

    public EncoderTuningService(
        FfmpegToolLocator locator, VideoSettingRepository settings, ILogger<EncoderTuningService> logger)
        : this(locator, settings, logger, run: null)
    {
    }

    /// <param name="run">Runs ffmpeg with these arguments and says whether it succeeded; tests replace it.</param>
    internal EncoderTuningService(
        FfmpegToolLocator locator,
        VideoSettingRepository settings,
        ILogger<EncoderTuningService> logger,
        Func<IReadOnlyList<string>, Task<bool>>? run)
    {
        _locator = locator;
        _settings = settings;
        _logger = logger;
        _run = run ?? RunAsync;
        _hardwareEncoder = new(FindHardwareEncoderAsync);
    }

    /// <summary>The first GPU encoder that encodes a few frames on this machine, or null.</summary>
    public Task<string?> HardwareEncoderAsync() => _hardwareEncoder.Value;

    /// <summary>
    /// The level a live goes out at with this setting: the one the setting names, or, when it says
    /// automatic, the one this machine was measured to keep up with at the size and rate of the live.
    /// </summary>
    public async Task<EncoderQuality> ResolveAsync(
        VideoSettingEntity setting, int width, int height, double frameRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);

        var chosen = VideoSettingQuality.Of(setting);
        if (chosen != EncoderQuality.Auto)
        {
            return chosen;
        }

        var codec = FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName);
        if (FfmpegCommandBuilder.PresetArguments(codec, EncoderQuality.Balanced).Count == 0)
        {
            // An encoder without levels has nothing to choose between.
            return EncoderQuality.Balanced;
        }

        width = width > 0 ? width : 1920;
        height = height > 0 ? height : 1080;
        frameRate = frameRate > 0 ? frameRate : 30;
        var bitrate = setting.VideoBitrate is > 0 ? setting.VideoBitrate.Value : 6_000_000;

        // One measure per encoder and picture, shared by every live that asks: the first one waits
        // for it, the ones after it have it at once.
        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{codec}|{width}x{height}|{frameRate:0.##}");
        var measure = _measured.GetOrAdd(
            key, _ => new Lazy<Task<EncoderQuality>>(() => MeasureAsync(codec, width, height, frameRate, bitrate)));
        return await measure.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fits the default settings to this machine: the H.264 encoder of the GPU when one works, x264
    /// otherwise, at the automatic level, without the x264 options that the GPU encoders refuse.
    /// Only the settings this application seeded are touched, and only when something changes.
    /// </summary>
    public async Task TuneDefaultsAsync(CancellationToken cancellationToken)
    {
        var hardware = await HardwareEncoderAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        // No GPU encoder and no x264 either is an ffmpeg that does not run at all: nothing has been
        // measured, so nothing is changed.
        if (hardware is null && !await _run(TrialFrames("libx264")).ConfigureAwait(false))
        {
            _logger.LogWarning("ffmpeg does not encode on this machine: the default settings are left as they are");
            return;
        }

        var encoder = hardware ?? "libx264";

        foreach (var setting in _settings.FindAll(new Dictionary<string, string>()))
        {
            if (!DefaultPlatforms.Contains(setting.DefaultPlatformConfiguration, StringComparer.Ordinal))
            {
                continue;
            }

            var changed = false;
            var current = FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName);
            if (IsH264(current) && current != encoder)
            {
                setting.VideoCodecName = encoder;
                changed = true;
            }

            if (setting.VideoSettingsOptions.RemoveAll(option => EncoderSpecificOptions.Contains(option.Key?.Trim())) > 0)
            {
                changed = true;
            }

            if (!setting.VideoSettingsOptions.Any(option => option.Key?.Trim() == VideoSettingQuality.OptionKey)
                || VideoSettingQuality.Of(setting) != EncoderQuality.Auto)
            {
                VideoSettingQuality.Set(setting, EncoderQuality.Auto);
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            setting.LastModified = DateTime.Now;
            _settings.Update(setting);
            _logger.LogInformation(
                "Default setting '{Title}' fitted to this machine: {Encoder}, automatic quality", setting.Title, encoder);
        }
    }

    private static bool IsH264(string codec) =>
        codec.Equals("libx264", StringComparison.OrdinalIgnoreCase)
        || codec.StartsWith("h264_", StringComparison.OrdinalIgnoreCase);

    private async Task<EncoderQuality> MeasureAsync(string codec, int width, int height, double frameRate, int bitrate)
    {
        var timings = new List<string>();
        foreach (var level in new[] { EncoderQuality.Balanced, EncoderQuality.Light })
        {
            var arguments = FfmpegCommandBuilder.BuildEncoderTrial(codec, level, width, height, frameRate, bitrate, TrialLength);
            var started = Stopwatch.GetTimestamp();
            var worked = await _run(arguments).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var speed = worked && elapsed > TimeSpan.Zero ? TrialLength / elapsed : 0;
            timings.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{level} {speed:0.0}x"));

            if (speed >= RequiredSpeed)
            {
                _logger.LogInformation(
                    "{Encoder} at {Width}x{Height} {Rate:0.##} fps: {Timings}, the live goes out at {Level}",
                    codec, width, height, frameRate, string.Join(", ", timings), level);
                return level;
            }
        }

        _logger.LogWarning(
            "{Encoder} at {Width}x{Height} {Rate:0.##} fps does not keep up with any level ({Timings}): the live goes out at the lightest",
            codec, width, height, frameRate, string.Join(", ", timings));
        return EncoderQuality.Light;
    }

    private async Task<string?> FindHardwareEncoderAsync()
    {
        foreach (var encoder in HardwareEncoders)
        {
            var worked = await _run(TrialFrames(encoder)).ConfigureAwait(false);

            if (worked)
            {
                _logger.LogInformation("GPU encoder found: {Encoder}", encoder);
                return encoder;
            }
        }

        _logger.LogInformation("No GPU encoder opens on this machine: the default settings encode on the CPU");
        return null;
    }

    /// <summary>Five black frames: enough for an encoder to say whether it opens on this machine.</summary>
    private static string[] TrialFrames(string encoder) =>
    [
        "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x144:r=30",
        "-frames:v", "5", "-c:v", encoder, "-f", "null", "-"
    ];

    private async Task<bool> RunAsync(IReadOnlyList<string> arguments)
    {
        try
        {
            await _locator.RunCapturedAsync(_locator.FfmpegPath, arguments, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
