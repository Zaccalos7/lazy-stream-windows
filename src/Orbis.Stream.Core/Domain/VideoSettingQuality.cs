namespace Orbis.Stream.Core.Domain;

/// <summary>
/// How much work the encoder puts into every frame: the one choice that trades the CPU (or the
/// GPU) a live costs against how clean its picture is at a given bitrate. Each encoder spells the
/// levels its own way (see <c>FfmpegCommandBuilder</c>), which is why the setting stores the level
/// and not a preset name: a preset of x264 is a command ffmpeg refuses on NVENC.
/// </summary>
public enum EncoderQuality
{
    /// <summary>Measured on this machine when the live starts: balanced when it keeps up, light otherwise.</summary>
    Auto,

    /// <summary>The lightest encode that is still clean: superfast, NVENC p1, QSV veryfast, AMF speed.</summary>
    Light,

    /// <summary>The streaming default: veryfast, NVENC p4, QSV medium, AMF balanced.</summary>
    Balanced,

    /// <summary>For a machine with room to spare: faster, NVENC p6, QSV slower, AMF quality.</summary>
    High
}

/// <summary>
/// The encoder quality of a video setting. Like the low latency switch it lives in the key/value
/// options of the setting (<see cref="OptionKey"/>), so a setting made before it existed needs no
/// migration: without the key the level is <see cref="EncoderQuality.Auto"/>.
/// </summary>
public static class VideoSettingQuality
{
    /// <summary>The option key the level is kept under, in the options of a video setting.</summary>
    public const string OptionKey = "encoderquality";

    /// <summary>
    /// The options that are decisions of this application and not ffmpeg options: they are read
    /// by the command builder and never written on the command line, where ffmpeg would stop at
    /// them as unknown.
    /// </summary>
    public static readonly IReadOnlySet<string> InternalKeys =
        new HashSet<string>(StringComparer.Ordinal) { OptionKey, VideoSettingLatency.OptionKey };

    public static EncoderQuality Parse(string? value) =>
        Enum.TryParse<EncoderQuality>(value?.Trim(), ignoreCase: true, out var quality) && Enum.IsDefined(quality)
            ? quality
            : EncoderQuality.Auto;

    /// <summary>The level of a setting; the last value wins, and none at all is automatic.</summary>
    public static EncoderQuality Of(VideoSettingEntity setting) =>
        Parse(setting.VideoSettingsOptions
            .Where(option => option.Key?.Trim() == OptionKey)
            .Select(option => option.Value)
            .LastOrDefault());

    /// <summary>Sets the level, replacing whatever the setting said before.</summary>
    public static void Set(VideoSettingEntity setting, EncoderQuality quality)
    {
        setting.VideoSettingsOptions.RemoveAll(option => option.Key?.Trim() == OptionKey);
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity { Key = OptionKey, Value = quality.ToString() });
    }
}
