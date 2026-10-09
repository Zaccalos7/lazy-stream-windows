namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The stream key of a platform that hands out a new one for every live and lets it expire:
/// TikTok, whose LIVE Producer shows a key that is good for that live only. A configuration of such
/// a platform keeps no key - one saved today would be refused tomorrow - and the key is asked for
/// when a live starts, every time.
/// <para>The key column of a configuration cannot be left empty (it is not null, and unique
/// together with the ingest), so the configuration stores a stand-in made of its channel: two
/// TikTok accounts on the same server are two configurations, and the same account twice is one.
/// The stand-in is never sent anywhere: a live started with it is refused (see StreamingService).</para>
/// </summary>
public static class LiveStreamKeys
{
    /// <summary>What a stand-in key starts with: no stream key of any platform does.</summary>
    public const string StandInPrefix = "asked-at-every-live:";

    /// <summary>The value the configurations of TikTok are stored under (MainChannelSettingModel).</summary>
    public const string TikTok = "tiktok";

    /// <summary>Whether a configuration asks for its key at every live: named TikTok, or with a TikTok ingest.</summary>
    public static bool IsAskedFor(string? platformStreamName, string? streamUrl) =>
        string.Equals(platformStreamName?.Trim(), TikTok, StringComparison.OrdinalIgnoreCase)
        || StreamPlatforms.Detect(streamUrl) == StreamPlatform.TikTok;

    /// <summary>The stand-in a configuration of the channel keeps in place of a key.</summary>
    public static string StandInFor(string? channelName) => StandInPrefix + (channelName ?? string.Empty).Trim();

    /// <summary>Whether a key is a stand-in rather than a key a platform handed out.</summary>
    public static bool IsStandIn(string? streamKey) =>
        streamKey?.StartsWith(StandInPrefix, StringComparison.Ordinal) == true;
}
