namespace Orbis.Stream.Core.Domain;

/// <summary>One selectable value: the number stored in the database, the label shown, the ffmpeg name.</summary>
public sealed record MediaOption(int Id, string Label, string FfmpegName);

/// <summary>
/// Maps the FFmpeg enum values stored in the database (<c>AV_CODEC_ID_*</c>, <c>AV_PIX_FMT_*</c>,
/// the numbers the JavaCV version and the React client wrote) to ffmpeg names. The same lists
/// feed the selects of the video settings page, so what the user picks is what ffmpeg receives.
/// </summary>
public static class FfmpegCodecCatalog
{
    public static readonly IReadOnlyList<MediaOption> VideoCodecs =
    [
        new(2, "MPEG2VIDEO", "mpeg2video"),
        new(12, "MPEG4", "mpeg4"),
        new(4, "H263", "h263"),
        new(21, "FLV1", "flv"),
        new(27, "H264", "libx264"),
        new(173, "H265 / HEVC", "libx265"),
        new(30, "THEORA", "libtheora"),
        new(70, "VC1", "vc1"),
        new(71, "WMV3", "wmv2"),
        new(139, "VP8", "libvpx"),
        new(167, "VP9", "libvpx-vp9"),
        new(225, "AV1", "libaom-av1"),
        new(7, "MJPEG", "mjpeg"),
        new(61, "PNG (video)", "png"),
        new(24, "DVVIDEO", "dvvideo"),
        new(25, "HUFFYUV", "huffyuv"),
        new(33, "FFV1 (lossless)", "ffv1"),
        new(147, "PRORES", "prores_ks"),
        new(99, "DNXHD", "dnxhd"),
        new(43, "CINEPAK", "cinepak"),
        new(28, "INDEO3", "indeo3"),
        new(116, "DIRAC", "vc2"),
        new(91, "VP6", "vp6"),
        new(92, "VP6F", "vp6f"),
        new(171, "WEBP (video)", "libwebp")
    ];

    public static readonly IReadOnlyList<MediaOption> AudioCodecs =
    [
        new(86016, "MP2", "mp2"),
        new(86017, "MP3", "libmp3lame"),
        new(86018, "AAC", "aac"),
        new(86019, "AC3", "ac3"),
        new(86020, "DTS", "dca"),
        new(86021, "Vorbis", "libvorbis"),
        new(86028, "FLAC", "flac"),
        new(86032, "ALAC", "alac"),
        new(86076, "Opus", "libopus"),
        new(86056, "E-AC3 (Dolby Digital Plus)", "eac3"),
        new(86060, "TrueHD", "truehd"),
        new(86045, "MLP", "mlp"),
        new(65536, "WAV (PCM S16LE)", "pcm_s16le"),
        new(65548, "PCM S24LE", "pcm_s24le"),
        new(65557, "PCM F32LE", "pcm_f32le"),
        new(86024, "WMA v2", "wmav2"),
        new(86053, "WMA Pro", "wmapro"),
        new(86048, "APE", "ape"),
        new(86044, "Musepack", "mpc8"),
        new(73728, "AMR-NB", "libopencore_amrnb"),
        new(73729, "AMR-WB", "libvo_amrwbenc")
    ];

    public static readonly IReadOnlyList<MediaOption> PixelFormats = BuildPixelFormats(
        "yuv420p", "yuyv422", "rgb24", "bgr24", "yuv422p", "yuv444p", "yuv410p", "yuv411p", "gray", "monow",
        "monob", "pal8", "yuvj420p", "yuvj422p", "yuvj444p", "uyvy422", "uyyvyy411", "bgr8", "bgr4", "bgr4_byte",
        "rgb8", "rgb4", "rgb4_byte", "nv12", "nv21", "argb", "rgba", "abgr", "bgra", "gray16be",
        "gray16le", "yuv440p", "yuvj440p", "yuva420p", "rgb48be", "rgb48le", "rgb565be", "rgb565le", "rgb555be", "rgb555le",
        "bgr565be", "bgr565le", "bgr555be", "bgr555le", "vaapi", "yuv420p16le", "yuv420p16be", "yuv422p16le", "yuv422p16be", "yuv444p16le",
        "yuv444p16be", "dxva2_vld", "rgb444le", "rgb444be", "bgr444le", "bgr444be", "ya8");

    public static readonly IReadOnlyList<string> VideoFormats = ["mp4", "mkv", "avi", "mov", "flv", "webm", "ts"];

    /// <summary>Encoder names grouped by vendor, as the React form offered them.</summary>
    public static readonly IReadOnlyList<(string Vendor, string[] Names)> VideoCodecNames =
    [
        ("CPU", ["libx264", "libx265", "libaom-av1", "libsvtav1", "libvpx", "libvpx-vp9", "mpeg4", "mjpeg", "prores_ks", "dnxhd", "ffv1", "huffyuv"]),
        ("NVIDIA", ["h264_nvenc", "hevc_nvenc", "av1_nvenc"]),
        ("AMD", ["h264_amf", "hevc_amf", "av1_amf"]),
        ("Intel", ["h264_qsv", "hevc_qsv", "av1_qsv"]),
        ("Apple", ["h264_videotoolbox", "hevc_videotoolbox"]),
        ("VAAPI", ["h264_vaapi", "hevc_vaapi", "av1_vaapi"])
    ];

    public static readonly IReadOnlyList<string> Presets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    public static readonly IReadOnlyList<(string Value, string Label)> Tunes =
    [
        ("zerolatency", "Zero Latency"),
        ("film", "Film"),
        ("animation", "Animation"),
        ("grain", "Grain"),
        ("stillimage", "Still Image"),
        ("fastdecode", "Fast Decode")
    ];

    /// <summary>
    /// The option key the low latency switch is kept under, in the key/value options of a video
    /// setting. A setting without it means what it always meant: low latency on.
    /// </summary>
    public const string LowLatencyOption = "lowlatency";

    /// <summary>
    /// Whether a setting asks the encoder for the smallest delay it can manage. Only an explicit
    /// no turns it off, so every setting made before the switch existed keeps its behaviour.
    /// </summary>
    public static bool IsLowLatency(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");

    public static string ResolveVideoCodecName(int? videoCodec, string? videoCodecName)
    {
        if (!string.IsNullOrWhiteSpace(videoCodecName))
        {
            return videoCodecName.Trim();
        }

        return Find(VideoCodecs, videoCodec) ?? "libx264";
    }

    public static string ResolveAudioCodecName(int? audioCodec, string? audioCodecName = null)
    {
        if (!string.IsNullOrWhiteSpace(audioCodecName))
        {
            return audioCodecName.Trim();
        }

        return Find(AudioCodecs, audioCodec) ?? "aac";
    }

    public static string ResolvePixelFormat(int? pixelFormat) => Find(PixelFormats, pixelFormat) ?? "yuv420p";

    /// <summary>
    /// The catalog entry an ffmpeg encoder name belongs to, or null for a vendor encoder the
    /// catalog does not list (<c>h264_nvenc</c> and the other hardware ones). The preview needs it
    /// to keep the abstract codec and the encoder name of a setting from drifting apart when only
    /// the encoder is changed.
    /// </summary>
    public static int? IdOfEncoderName(string? encoderName)
    {
        if (string.IsNullOrWhiteSpace(encoderName))
        {
            return null;
        }

        var name = encoderName.Trim();
        return VideoCodecs.FirstOrDefault(option => option.FfmpegName == name)?.Id;
    }

    /// <summary>Label of a catalog entry, so the preview can name a pixel format without ffprobe.</summary>
    public static string? LabelOf(IReadOnlyList<MediaOption> options, int? id) =>
        id is null ? null : options.FirstOrDefault(option => option.Id == id.Value)?.Label;

    private static string? Find(IReadOnlyList<MediaOption> options, int? id) =>
        id is null ? null : options.FirstOrDefault(option => option.Id == id.Value)?.FfmpegName;

    private static MediaOption[] BuildPixelFormats(params string[] names) =>
        names.Select((name, index) => new MediaOption(index, name.ToUpperInvariant(), name)).ToArray();
}
