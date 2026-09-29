using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Pages;

/// <summary>Pager of the lists: a window of five pages around the current one, as the React tables had.</summary>
public sealed record PagerModel(int Current, int Total, Func<int, string> Href)
{
    public IEnumerable<int> Window
    {
        get
        {
            var first = Math.Clamp(Current - 2, 0, Math.Max(Total - 5, 0));
            return Enumerable.Range(first, Math.Min(5, Total));
        }
    }
}

public static class LiveStatusView
{
    public static string Key(LiveStatus? status) => status switch
    {
        LiveStatus.Live => "liveStatusLive",
        LiveStatus.Ended => "liveStatusEnded",
        LiveStatus.Error => "liveStatusError",
        LiveStatus.Stopped => "liveStatusStopped",
        _ => "liveStatusOffline"
    };

    public static string Css(LiveStatus? status) => status switch
    {
        LiveStatus.Live => "live",
        LiveStatus.Ended => "ended",
        LiveStatus.Error => "error",
        LiveStatus.Stopped => "stopped",
        _ => string.Empty
    };

    public static string Date(DateTime? value, string format) =>
        value?.ToString(format, System.Globalization.CultureInfo.InvariantCulture) ?? "N/A";
}

/// <summary>
/// Public page of a live on its platform, built from the RTMP ingest stored with the live: the
/// ingest URL is the only platform evidence a video row carries, so a configuration that points
/// to a custom ingest (a service this application does not know) has no link.
/// </summary>
public static class LiveLinkView
{
    /// <summary>Platform of an ingest URL (<c>twitch</c>, <c>youtube</c>), or null if unknown.</summary>
    public static string? PlatformOf(string? streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        var platform = MainChannelSettingModel.Platforms
            .FirstOrDefault(known => string.Equals(known.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase));

        return platform.Label is null ? null : platform.Value;
    }

    /// <summary>Name the platform shows in its own interface (Twitch, YouTube).</summary>
    public static string? LabelOf(string? platform) =>
        MainChannelSettingModel.Platforms
            .FirstOrDefault(known => known.Value == platform).Label;

    /// <summary>Icon of the platform: the Twitch mark, the YouTube play mark, the generic play.</summary>
    public static string GlyphOf(string? platform) => platform == "twitch" ? "\uE93E" : "\uE714";

    /// <summary>
    /// Page of the live of a platform channel: on Twitch it is the channel, on YouTube the live
    /// tab of the handle. A YouTube channel has no public page keyed by its ingest data, so the
    /// handle is what the user typed in the configuration.
    /// </summary>
    /// <param name="channelName">
    /// The channel of the configuration. The form of the configuration keeps the platform in the
    /// field named platform stream name (the choice between Twitch and YouTube) and the channel
    /// here, so a live started from the wizard has "twitch" as platform stream name.
    /// </param>
    /// <param name="platformStreamName">
    /// The same channel, for a live that came from the API, whose record names the channel there.
    /// </param>
    public static string? UrlOf(string? streamUrl, string? channelName, string? platformStreamName)
    {
        var name = (channelName ?? platformStreamName)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return PlatformOf(streamUrl) switch
        {
            "twitch" => "https://www.twitch.tv/" + Uri.EscapeDataString(name),
            "youtube" => "https://www.youtube.com/@" + Uri.EscapeDataString(name.StartsWith('@') ? name[1..] : name) + "/live",
            _ => null
        };
    }
}
