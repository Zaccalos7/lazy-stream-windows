namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The stream keys that stop working by themselves: Facebook, whose Live Producer writes a key for
/// the broadcast being started and refuses the one of any other - a key kept for a week is a key
/// the ingest will not take, and the live ends before a frame of it is seen.
/// <para>It is not the platform of <see cref="LiveStreamKeys"/>, which asks for a key at every live
/// because none of them is ever good twice (TikTok). A Facebook key is good for a whole day of
/// lives, so the configuration keeps it and what the page has to know is how old it is: the moment
/// it was saved is stored next to it (<c>setting.stream_key_saved_at</c>, written by
/// <see cref="Services.SettingService"/> whenever the key itself changes).</para>
/// <para>A configuration whose key has no saved moment has one that was saved before that column
/// existed, and that is taken to be expired: a Facebook key that old is not one anybody can still
/// be streaming with, and a warning that turns out to be wrong costs the user one paste.</para>
/// </summary>
public static class ExpiringStreamKeys
{
    /// <summary>The value a Facebook configuration is stored under (MainChannelSettingModel).</summary>
    public const string Facebook = "facebook";

    /// <summary>
    /// How long a Facebook stream key is good for. A day is what the producer's own text says
    /// about the key it hands out, and it is the whole of it: there is no way to ask the platform
    /// whether a key is still live, so the age of the key is the only thing there is to go on.
    /// </summary>
    public static readonly TimeSpan FacebookLifetime = TimeSpan.FromDays(1);

    /// <summary>Whether the keys of a configuration stop working: named Facebook, or with a Facebook ingest.</summary>
    public static bool Expires(string? platformStreamName, string? streamUrl) =>
        string.Equals(platformStreamName?.Trim(), Facebook, StringComparison.OrdinalIgnoreCase)
        || StreamPlatforms.Detect(streamUrl) == StreamPlatform.Facebook;

    /// <summary>
    /// Whether the key of a configuration can no longer be streamed with: the platform does not
    /// expire its keys, or it does and the key was saved more than a lifetime ago - or when that
    /// is not known at all.
    /// </summary>
    public static bool IsExpired(string? platformStreamName, string? streamUrl, DateTime? savedAt, DateTime now)
    {
        if (!Expires(platformStreamName, streamUrl)) return false;
        return savedAt is not { } saved || now - saved >= FacebookLifetime;
    }

    /// <summary>How much of the life of the key is left, for the page to say how soon it has to be replaced.</summary>
    public static TimeSpan AgeOf(DateTime? savedAt, DateTime now) =>
        savedAt is { } saved ? now - saved : TimeSpan.Zero;
}