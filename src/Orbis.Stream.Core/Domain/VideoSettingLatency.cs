namespace Orbis.Stream.Core.Domain;

/// <summary>
/// The low latency switch of a video setting: the encoder defaults that buy the smallest delay at
/// the cost of the rate control looking ahead.
/// <para>It lives in the key/value options of the setting, under <see cref="OptionKey"/>, so a
/// setting made before it existed keeps meaning what it meant: low latency on. That is the whole
/// of the backwards compatibility, and it is why a missing value is not the same as an off one.</para>
/// </summary>
public static class VideoSettingLatency
{
    /// <summary>The option key the switch is kept under, in the options of a video setting.</summary>
    public const string OptionKey = "lowlatency";

    /// <summary>
    /// Whether a stored value asks for low latency. Only an explicit no turns it off.
    /// </summary>
    public static bool IsOn(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");

    /// <summary>Whether the options of a setting ask for low latency; the last value wins.</summary>
    public static bool IsOn(IEnumerable<VideoSettingsOptionEntity> options) =>
        IsOn(options
            .Where(option => option.Key?.Trim() == OptionKey)
            .Select(option => option.Value)
            .LastOrDefault());

    /// <summary>Whether a setting asks for low latency; a setting without the key means on.</summary>
    public static bool IsOn(VideoSettingEntity setting) => IsOn(setting.VideoSettingsOptions);

    /// <summary>
    /// Turns the switch on or off in the options of a setting, replacing whatever it said before,
    /// so the option is never stored twice and the last one always answers.
    /// </summary>
    public static void Set(VideoSettingEntity setting, bool enabled)
    {
        setting.VideoSettingsOptions.RemoveAll(option => option.Key?.Trim() == OptionKey);
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity
        {
            Key = OptionKey,
            Value = enabled ? "1" : "0"
        });
    }
}