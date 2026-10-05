using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

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

/// <summary>
/// The scene composer: the scene it opens on, if any (after a refused start), and what it edits.
/// A <see cref="Layout"/> composer draws the empty slots of a layout and saves them for later; the
/// one of the live wizard fills the slots of a layout with sources and starts the live from them.
/// </summary>
public sealed record SceneComposerModel(long? Scene, bool Layout = false);

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

        // A configuration saved with the RTMPS url of before still points at YouTube: it is compared
        // the way it is streamed, with the host the stream is sent to.
        var normalized = StreamPlatforms.NormalizeIngestUrl(streamUrl);
        var platform = MainChannelSettingModel.Platforms
            .FirstOrDefault(known => string.Equals(known.StreamUrl, normalized, StringComparison.OrdinalIgnoreCase));

        return platform.Label is null ? null : platform.Value;
    }

    /// <summary>
    /// The platform mark, as the outline of an SVG so it is drawn in the colour of the page rather
    /// than as a glyph of an icon font: the Twitch and the YouTube mark are brands of their own and
    /// the Segoe glyphs they were drawn with were neither.
    /// </summary>
    public static string MarkupOf(string? platform) => platform switch
    {
        "twitch" => TwitchMark,
        "youtube" => YoutubeMark,
        _ => PlayMark
    };

    /// <summary>The class that colours the mark of a platform.</summary>
    public static string ClassOf(string? platform) => platform switch
    {
        "twitch" => "platform-twitch",
        "youtube" => "platform-youtube",
        _ => "platform-generic"
    };

    /// <summary>The Twitch mark.</summary>
    private const string TwitchMark =
        "M11.571 4.714h1.715v5.143H11.57zm4.715 0H18v5.143h-1.714zM6 0L1.714 4.286v15.428h5.143V24l4.286-4.286h3.428L22.286 12V0zm14.571 11.143l-3.428 3.428h-3.429l-3 3v-3H6.857V1.714h13.714Z";

    /// <summary>The YouTube mark.</summary>
    private const string YoutubeMark =
        "M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z";

    /// <summary>The play mark of a platform this application does not know.</summary>
    private const string PlayMark = "M8 5v14l11-7z";

    /// <summary>Name the platform shows in its own interface (Twitch, YouTube).</summary>
    public static string? LabelOf(string? platform) =>
        MainChannelSettingModel.Platforms
            .FirstOrDefault(known => known.Value == platform).Label;

    /// <summary>
    /// Page of a platform channel: on Twitch the channel, on YouTube the page of the channel, which
    /// the address is built from.
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
            "youtube" => YouTubeChannelUrl(name),
            _ => null
        };
    }

    /// <summary>
    /// The page of a YouTube channel. A channel has two public addresses and they do not take each
    /// other's names: the page of a handle is <c>/@handle</c>, and the page of a channel id is
    /// <c>/channel/UC...</c>. <c>/channel/madajeeita207</c> is not the channel of the handle
    /// madajeeita207, it is a page that does not exist, so which address to build is decided by
    /// what the configuration holds rather than by picking one.
    /// <para>This is the one place a YouTube channel address is built. The preview resolves the live
    /// to watch out of it, and the live page links to it, so the two cannot end up disagreeing
    /// about what the channel of a configuration is called.</para>
    /// </summary>
    public static string YouTubeChannelUrl(string name)
    {
        var channel = name.Trim();
        return IsYouTubeChannelId(channel)
            ? "https://www.youtube.com/channel/" + Uri.EscapeDataString(channel)
            : "https://www.youtube.com/@" + Uri.EscapeDataString(channel.TrimStart('@'));
    }

    /// <summary>
    /// A channel id is <c>UC</c> and twenty two more characters of the alphabet YouTube encodes
    /// them with, which is what tells one apart from a handle: a handle is free to be any length,
    /// but it is never this shape.
    /// </summary>
    private static bool IsYouTubeChannelId(string name) =>
        name.Length == 24
        && name.StartsWith("UC", StringComparison.Ordinal)
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
