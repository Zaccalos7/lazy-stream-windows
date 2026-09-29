using System.Globalization;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Everything needed to transcode one file into the live destination.</summary>
public sealed record FfmpegStreamRequest(
    string InputPath,
    string OutputUrl,
    MediaProbeResult Probe,
    VideoSettingEntity Setting);

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
        var frameRate = probe.FrameRate > 0 ? probe.FrameRate : 25d;

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-re",
            "-i",
            request.InputPath
        };

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
    /// Port of the stream url composition of <c>StreamService#startLive</c>: the key is appended
    /// to the url, adding the separator only when needed.
    /// </summary>
    public static string BuildStreamingUrl(string streamUrl, string streamKey)
    {
        ArgumentNullException.ThrowIfNull(streamUrl);
        ArgumentNullException.ThrowIfNull(streamKey);

        return streamUrl.EndsWith('/') ? streamUrl + streamKey : streamUrl + "/" + streamKey;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
