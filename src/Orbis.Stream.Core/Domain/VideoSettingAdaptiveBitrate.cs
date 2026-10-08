namespace Orbis.Stream.Core.Domain;

/// <summary>
/// The adaptive bitrate switch of a video setting: whether the bitrate of a live follows what the
/// network carries (the ladder of the live, <c>BitrateLadder</c>) or stays the bitrate of the
/// setting whatever the network does. On, a network that cannot carry the live brings the bitrate
/// down instead of leaving the viewers to rebuffer, and it climbs back once the network carries it
/// again; it never goes above the bitrate of the setting.
/// <para>Like the low latency switch it lives in the key/value options of the setting, under
/// <see cref="OptionKey"/>, so a setting made before it existed needs no migration: without the
/// key the switch is on, which is how every live went out before it could be turned off.</para>
/// </summary>
public static class VideoSettingAdaptiveBitrate
{
    /// <summary>The option key the switch is kept under, in the options of a video setting.</summary>
    public const string OptionKey = "adaptivebitrate";

    /// <summary>Whether a stored value lets the bitrate follow the network. Only an explicit no turns it off.</summary>
    public static bool IsOn(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");

    /// <summary>Whether a setting lets its bitrate follow the network; the last value wins, and none at all is on.</summary>
    public static bool IsOn(VideoSettingEntity setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        return IsOn(setting.VideoSettingsOptions
            .Where(option => option.Key?.Trim() == OptionKey)
            .Select(option => option.Value)
            .LastOrDefault());
    }

    /// <summary>
    /// Turns the switch on or off in the options of a setting, replacing whatever it said before,
    /// so the option is never stored twice and the last one always answers.
    /// </summary>
    public static void Set(VideoSettingEntity setting, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(setting);

        setting.VideoSettingsOptions.RemoveAll(option => option.Key?.Trim() == OptionKey);
        setting.VideoSettingsOptions.Add(new VideoSettingsOptionEntity
        {
            Key = OptionKey,
            Value = enabled ? "1" : "0"
        });
    }
}
