using System.Globalization;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Everything needed to transcode one file into the live destination.</summary>
public sealed record FfmpegStreamRequest(
    string InputPath,
    string OutputUrl,
    MediaProbeResult Probe,
    VideoSettingEntity Setting,
    TimeSpan ResumeFrom = default);

/// <summary>
/// Translates the <c>FFmpegFrameRecorder</c> configuration of <c>StreamService</c> into the
/// equivalent ffmpeg command line. The Java version decoded every frame in Java and re-encoded
/// it with real time pacing; <c>-re</c> gives the same pacing to the ffmpeg process.
/// </summary>
public static class FfmpegCommandBuilder
{
    public static IReadOnlyList<string> Build(FfmpegStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setting = request.Setting;
        var probe = request.Probe;

        // The rate the encoder is asked for: what the setting asks for, otherwise the rate ffprobe
        // read from the file (25 is the fallback of the probe, kept for a probe that knows nothing).
        var output = ResolveOutput(setting, probe);
        var frameRate = output.FrameRate;

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",

            // A transcode that starts again with new parameters writes over what the previous one
            // sent: with a real ingest there is nothing to overwrite, and with a destination on
            // disk ffmpeg would otherwise stop to ask a question nobody is there to answer.
            "-y",

            "-loglevel",
            "error",

            // Where the transcode is, twice a second: that is the only place a running ffmpeg tells
            // how far it got, and a stop has to leave the position on the video row to resume there.
            "-progress",
            "pipe:1",
            "-nostats",
            "-stats_period",
            "0.2",
            "-re"
        };

        // Where to carry on from, before the input: as an input option ffmpeg seeks there and starts
        // reading immediately, where an output option would decode and throw the frames away.
        if (request.ResumeFrom > TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(Seconds(request.ResumeFrom));
        }

        arguments.Add("-i");
        arguments.Add(request.InputPath);

        // JavaCV mapped the grabbed video/audio streams of the input file.
        arguments.Add("-map");
        arguments.Add("0:v:0");

        if (probe.HasAudio)
        {
            arguments.Add("-map");
            arguments.Add("0:a:0?");
        }

        if (!string.IsNullOrWhiteSpace(setting.VideoFormat))
        {
            arguments.Add("-f");
            arguments.Add(setting.VideoFormat.Trim());
        }

        arguments.Add("-c:v");
        arguments.Add(FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName));

        arguments.Add("-pix_fmt");
        arguments.Add(FfmpegCodecCatalog.ResolvePixelFormat(setting.PixelFormat));

        arguments.Add("-r");
        arguments.Add(Number(frameRate));

        // Only when the setting asks for a resolution of its own: without it the frame is
        // re-encoded exactly as the file is, which is what every stream did before the field.
        if (ScaleFilter(setting) is { } scale)
        {
            arguments.Add("-vf");
            arguments.Add(scale);
        }
        if (setting.VideoBitrate is > 0)
        {
            arguments.Add("-b:v");
            arguments.Add(setting.VideoBitrate.Value.ToString(CultureInfo.InvariantCulture));
        }

        // Twitch strictly requires a keyframe every 2 seconds: gop = fps * gopSize.
        if (setting.GopSize is > 0)
        {
            arguments.Add("-g");
            arguments.Add(((int)(frameRate * setting.GopSize.Value)).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var option in setting.VideoSettingsOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Key))
            {
                continue;
            }

            arguments.Add($"-{option.Key!.Trim()}");
            if (option.Value is not null)
            {
                arguments.Add(option.Value);
            }
        }

        if (probe.HasAudio && setting.AudioSetting is not null)
        {
            arguments.Add("-c:a");
            arguments.Add(FfmpegCodecCatalog.ResolveAudioCodecName(setting.AudioSetting.AudioCodec));

            if (setting.AudioSetting.AudioBitrate is > 0)
            {
                arguments.Add("-b:a");
                arguments.Add(setting.AudioSetting.AudioBitrate.Value.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add("-ar");
            arguments.Add("44100");

            if (probe.AudioChannels > 0)
            {
                arguments.Add("-ac");
                arguments.Add(probe.AudioChannels.ToString(CultureInfo.InvariantCulture));
            }
        }

        arguments.Add(request.OutputUrl);

        return arguments;
    }

    /// <summary>
    /// What the encoder will be asked to produce for a file: the resolution and the frame rate of
    /// the setting when it has one, the ones ffprobe read otherwise. This is the single place that
    /// decides it, so the command line and the numbers the preview shows can never disagree.
    /// </summary>
    public static MediaOutput ResolveOutput(VideoSettingEntity setting, MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(probe);

        return new MediaOutput(
            setting.VideoWidth is > 0 ? Even(setting.VideoWidth.Value) : probe.Width,
            setting.VideoHeight is > 0 ? Even(setting.VideoHeight.Value) : probe.Height,
            setting.FrameRate is > 0 ? setting.FrameRate.Value : probe.FrameRate > 0 ? probe.FrameRate : 25d);
    }

    /// <summary>
    /// The <c>scale</c> filter of a setting that asks for a resolution, or null when it keeps the
    /// one of the file. Both dimensions are rounded down to an even number: every pixel format a
    /// platform accepts (yuv420p above all) refuses an odd frame size, and a live that dies on the
    /// first frame of a new resolution is a much worse answer than one pixel of padding.
    /// </summary>
    public static string? ScaleFilter(VideoSettingEntity setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (setting.VideoWidth is not > 0 || setting.VideoHeight is not > 0)
        {
            return null;
        }

        // The name of the filter is part of the value: <c>-vf 160:120</c> asks ffmpeg for a filter
        // called "160:120", which is why a live that had never rescaled looked fine and the first
        // one that did died on the first frame instead of sending the smaller resolution.
        return $"scale={Even(setting.VideoWidth.Value)}:{Even(setting.VideoHeight.Value)}";
    }

    /// <summary>Port of the stream url composition of <c>StreamService#startLive</c>: the key is appended
    /// to the url, adding the separator only when needed.
    /// </summary>
    public static string BuildStreamingUrl(string streamUrl, string streamKey)
    {
        ArgumentNullException.ThrowIfNull(streamUrl);
        ArgumentNullException.ThrowIfNull(streamKey);

        return streamUrl.EndsWith('/') ? streamUrl + streamKey : streamUrl + "/" + streamKey;
    }

    private static int Even(int value) => value - (value % 2);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan position) =>
        Number(Math.Max(0d, position.TotalSeconds));
}
