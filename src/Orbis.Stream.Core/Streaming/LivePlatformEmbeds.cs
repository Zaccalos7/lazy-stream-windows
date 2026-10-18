using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Pages;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The player of a platform, for a page that wants to show the live as the viewers see it rather
/// than as the encoder sees it: the platform it goes on, the channel of the configuration, and the
/// address of the player that plays it.
/// </summary>
/// <param name="VideoId">The video on air, where the platform addresses a live by one (YouTube).</param>
public sealed record LivePlatformEmbed(string Platform, string Channel, string Url, string? VideoId = null, string? WatchUrl = null);

/// <summary>
/// Public players for Twitch, YouTube, Kick and Facebook Live.
///
/// The channel is the only thing the configuration stores about where a live goes, and it is stored
/// where it is easy to mistake it for something else: a live started from the wizard keeps the
/// choice between the two platforms in the field named platform stream name ("twitch", "youtube")
/// and the channel itself in the channel name. So the channel name is what is played here, exactly
/// as it is for the link out to the page of the channel.
///
/// Twitch is played by its channel, and YouTube is not: its player takes the id of the video, and
/// the live of a channel only has that id once the broadcast has started, so the id is looked up in
/// the page of the streams of the channel and the page of the video is asked whether it is really on
/// air. Both answers are held for a while, because the preview page asks again and again while it
/// is open and the platform pages are the slow part of this.
/// </summary>
public sealed class LivePlatformEmbeds
{
    /// <summary>How long the id of a live found on YouTube is played without asking again.</summary>
    private static readonly TimeSpan OnAir = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long "the channel is not on air" is believed before asking again. Shorter than
    /// <see cref="OnAir"/> on purpose: this is the answer a page gets a moment before a live starts,
    /// and a channel that is not live has to be noticed becoming live while the preview is open.
    /// </summary>
    private static readonly TimeSpan OffAir = TimeSpan.FromSeconds(10);

    // YouTube changes renderer layouts and JSON property order. Read the data rather than
    // depending on adjacent fields in the HTML. Window assignments occur alongside var declarations.
    private static readonly Regex InitialData = new(
        @"(?:var\s+ytInitialData|window\s*\[\s*[""']ytInitialData[""']\s*\]|ytInitialData)\s*=\s*",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex PlayerResponse = new(
        @"(?:var\s+ytInitialPlayerResponse|window\s*\[\s*[""']ytInitialPlayerResponse[""']\s*\]|ytInitialPlayerResponse)\s*=\s*",
        RegexOptions.CultureInvariant);

    private static JsonDocument? ReadData(string html, Regex assignment)
    {
        var match = assignment.Match(html);
        var json = match.Success ? html.AsSpan(match.Index + match.Length) : html.AsSpan().TrimStart();
        if (!match.Success && (json.IsEmpty || json[0] != '{')) return null;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json.ToString()));
        // Parse only the assigned JSON value, even if more JavaScript follows in the same script.
        return JsonDocument.ParseValue(ref reader);
    }

    /// <summary>
    /// What YouTube says about a video, before it localises anything on the page: the details of
    /// the video say <c>isLive</c> and those of the broadcast <c>isLiveNow</c>, and both only while
    /// it is on air. Not <c>isLiveContent</c>, which a broadcast keeps once it has ended: it is the
    /// field that makes the last live of a channel off air look like a live.
    /// </summary>
    private static readonly Regex OnAirNow = new(
        "\"isLive(?:Now)?\"\\s*:\\s*true",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// What the page of a video says about a broadcast once it is over: still a live content, no
    /// longer live. It is the positive answer "ended" - a page that says neither is a page this
    /// application did not understand, and that is not taken to mean anything.
    /// </summary>
    private static readonly Regex LiveContent = new(
        "\"isLiveContent\"\\s*:\\s*true",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly ILogger<LivePlatformEmbeds> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (string? VideoId, string? Failure, DateTimeOffset Asked)> _youtube =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One question at a time: a page asking again while the first one is still open.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LivePlatformEmbeds(HttpClient http, ILogger<LivePlatformEmbeds> logger)
        : this(http, logger, TimeProvider.System)
    {
    }

    internal LivePlatformEmbeds(HttpClient http, ILogger<LivePlatformEmbeds> logger, TimeProvider time)
    {
        _http = http;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// The answer for a live that cannot be played where it goes, and why. There is a difference
    /// between a channel that is not on air and a channel this application failed to ask about, and
    /// a page told the first when the second is what happened repeats it for ever, because the
    /// second is what happens every time. The reason is what keeps the two apart.
    /// </summary>
    public sealed record LivePlatformLookup(LivePlatformEmbed? Embed, string? Reason)
    {
        public static LivePlatformLookup Nothing { get; } = new(null, null);

        public static LivePlatformLookup Of(LivePlatformEmbed? embed) => new(embed, null);

        public static LivePlatformLookup Failed(string reason) => new(null, reason);
    }

    /// <summary>
    /// The player of the platform the live is going to, or nothing when there is nothing to play:
    /// a configuration pointing at an ingest this application does not know, a live that is not
    /// running, or a YouTube channel that is not on air. What could not be asked is said apart from
    /// what was asked and found empty, so a page is never told a channel is off air because a
    /// request to the platform failed.
    /// </summary>
    public async Task<LivePlatformLookup> ResolveAsync(
        string? streamUrl,
        string? channelName,
        string? platformStreamName,
        string? host,
        CancellationToken cancellationToken,
        string? watchUrl = null)
    {
        var channel = (channelName ?? platformStreamName)?.Trim();
        // Facebook needs the individual public video, not the producer URL or a page name.
        if (LiveLinkView.PlatformOf(streamUrl) == "facebook")
        {
            var video = FacebookVideoUrl(watchUrl ?? channel);
            return video is null
                ? LivePlatformLookup.Failed("Facebook Live requires the public URL of the live video")
                : LivePlatformLookup.Of(new LivePlatformEmbed("facebook", channel ?? string.Empty,
                    "https://www.facebook.com/plugins/video.php?href=" + Uri.EscapeDataString(video) +
                    "&show_text=false&autoplay=true&mute=true&width=640", WatchUrl: video));
        }
        if (string.IsNullOrEmpty(channel))
        {
            return LiveLinkView.PlatformOf(streamUrl) == "youtube"
                ? LivePlatformLookup.Failed("YouTube requires a channel handle or channel id")
                : LivePlatformLookup.Nothing;
        }

        return LiveLinkView.PlatformOf(streamUrl) switch
        {
            "twitch" => LivePlatformLookup.Of(new LivePlatformEmbed("twitch", channel, TwitchUrl(channel, host))),
            "youtube" => await YouTubeAsync(channel, cancellationToken),
            "kick" => KickChannel(channel) is { } kick
                ? LivePlatformLookup.Of(new LivePlatformEmbed("kick", kick,
                    "https://player.kick.com/" + Uri.EscapeDataString(kick) + "?autoplay=true&muted=true",
                    WatchUrl: "https://kick.com/" + Uri.EscapeDataString(kick)))
                : LivePlatformLookup.Failed("Kick requires the channel username or URL"),
            _ => LivePlatformLookup.Nothing
        };
    }

    /// <summary>Accept a Kick username or its public channel URL.</summary>
    public static string? KickChannel(string value)
    {
        var channel = value.Trim();
        if (Uri.TryCreate(channel, UriKind.Absolute, out var url))
        {
            if (url.Scheme is not ("https" or "http") || !url.IsDefaultPort || url.UserInfo.Length > 0 ||
                url.Host is not ("kick.com" or "www.kick.com")) return null;
            channel = url.AbsolutePath.Trim('/');
        }
        channel = channel.TrimStart('@').ToLowerInvariant();
        return Regex.IsMatch(channel, @"\A[a-z0-9_-]+\z", RegexOptions.CultureInvariant) ? channel : null;
    }

    /// <summary>Canonical public video URL. Page names and Producer URLs do not identify a live.</summary>
    public static string? FacebookVideoUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http") || !url.IsDefaultPort || url.UserInfo.Length > 0 ||
            url.Host is not ("facebook.com" or "www.facebook.com" or "m.facebook.com" or "web.facebook.com")) return null;

        var path = url.AbsolutePath.TrimEnd('/');
        var match = Regex.Match(path, @"\A/(?:[^/]+/)?videos/(?:[^/]+/)?(?<id>[0-9]+)\z", RegexOptions.CultureInvariant);
        var id = match.Success ? match.Groups["id"].Value : null;
        if (id is null && path is ("" or "/watch" or "/watch/live" or "/video.php" or "/live"))
        {
            var query = QueryHelpers.ParseQuery(url.Query);
            if (query.TryGetValue("v", out var video) && video.Count == 1 &&
                Regex.IsMatch(video.ToString(), @"\A[0-9]+\z", RegexOptions.CultureInvariant)) id = video.ToString();
        }
        return id is null ? null : "https://www.facebook.com/watch/?v=" + id;
    }

    /// <summary>
    /// The Twitch player of a channel. Twitch plays the embed only on a page it is told the name of
    /// (<c>parent</c>), and it takes one name: the host the request came in on, which is the name
    /// of the page the player is about to be drawn into.
    /// </summary>
    public static string TwitchUrl(string channel, string? host)
    {
        var parent = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();

        // A Twitch channel is its name in lower case, and a name in another case is a channel that
        // does not exist to the player even when the page of it opens.
        return "https://player.twitch.tv/?channel=" + Uri.EscapeDataString(channel.Trim().ToLowerInvariant()) +
            "&parent=" + Uri.EscapeDataString(parent) +
            "&muted=true&autoplay=true&playsinline=true";
    }

    /// <summary>The YouTube player of a live, once the id of the video is known.</summary>
    public Task<LivePlatformLookup> ResolveYouTubeChannelAsync(string channel, CancellationToken cancellationToken) =>
        YouTubeAsync(channel, cancellationToken);

    private async Task<LivePlatformLookup> YouTubeAsync(string channel, CancellationToken cancellationToken)
    {
        var handle = channel.Trim().TrimStart('@');
        if (handle.Length == 0)
        {
            return LivePlatformLookup.Nothing;
        }

        var found = await LiveVideoAsync(handle, cancellationToken);
        if (found.VideoId is not { } videoId)
        {
            // Nothing to play, and the difference between a channel that is off air and a page this
            // application could not read is the whole of what the user is looking for.
            return found.Failure is { } failure
                ? LivePlatformLookup.Failed(failure)
                : LivePlatformLookup.Nothing;
        }

        return LivePlatformLookup.Of(new LivePlatformEmbed(
            "youtube",
            handle,
            // Muted and inline, so the browser lets it start on its own: a preview is opened
            // without a gesture on the player and autoplay of sound is not one it will allow.
            $"https://www.youtube.com/embed/{videoId}?autoplay=1&mute=1&playsinline=1&rel=0&enablejsapi=1",
            videoId));
    }

    /// <summary>
    /// The id of the video a channel is broadcasting right now, or nothing. <c>Failure</c> is what
    /// could not be asked, as opposed to what was asked and found empty.
    /// </summary>
    public async Task RefreshYouTubeAsync(string channel, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _youtube.TryRemove(channel.Trim().TrimStart('@'), out _); }
        finally { _gate.Release(); }
    }

    private async Task<(string? VideoId, string? Failure)> LiveVideoAsync(string handle, CancellationToken cancellationToken)
    {
        if (Remembered(handle) is { } fresh)
        {
            return fresh;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Remembered(handle) is { } again)
            {
                return again;
            }

            var (videoId, failure) = await LookUpAsync(handle, cancellationToken);
            _youtube[handle] = (videoId, failure, _time.GetUtcNow());
            return (videoId, failure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception problem)
        {
            // A stale success cannot confirm that a broadcast is still on air.
            _logger.LogWarning(problem, "The live of the YouTube channel {Channel} could not be looked up.", handle);
            const string failure = "YouTube could not be asked for the streams of the channel";
            _youtube[handle] = (null, failure, _time.GetUtcNow());
            return (null, failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The answer for a channel that was asked recently enough to still be a good one.</summary>
    private (string? VideoId, string? Failure)? Remembered(string handle)
    {
        if (!_youtube.TryGetValue(handle, out var entry))
        {
            return null;
        }

        var age = _time.GetUtcNow() - entry.Asked;
        return age < (entry.VideoId is null ? OffAir : OnAir) ? (entry.VideoId, entry.Failure) : null;
    }

    /// <summary>
    /// The video of the top of the page of the streams of a channel, asked whether it is live before
    /// it is handed out: a channel that is not on air lists its last broadcast there too, and a
    /// player given the id of an old video plays an old video, which is a worse answer than none.
    /// </summary>
    private async Task<(string? VideoId, string? Failure)> LookUpAsync(string handle, CancellationToken cancellationToken)
    {
        var streamsUrl = LiveLinkView.YouTubeChannelUrl(handle) + "/streams";
        var streams = await ReadAsync(streamsUrl, cancellationToken);
        var candidates = streams is null ? null : CandidatesOf(streams);
        if (candidates is null)
        {
            // Some regions serve a consent document with HTTP 200. Try the public live route,
            // then the channel with YouTube's public consent-bypass redirect parameter. Neither
            // uses a signed-in account nor treats the consent page as an offline broadcast.
            var direct = await ReadAsync(LiveLinkView.YouTubeChannelUrl(handle) + "/live?ucbcb=1", cancellationToken);
            if (LiveVideoOf(direct) is { } liveVideo) return (liveVideo, null);
            streams = await ReadAsync(streamsUrl + "?ucbcb=1", cancellationToken);
            candidates = streams is null ? null : CandidatesOf(streams);
            if (candidates is null)
                return (null, "YouTube channel and live pages could not be verified (possibly a consent page)");
        }

        string? failure = null;
        // A scheduled event or another completed broadcast can precede the live in the grid.
        // Check a bounded set, with entries carrying a LIVE badge first, and verify every watch page.
        foreach (var candidate in candidates.Take(5))
        {
            var watch = await ReadAsync($"https://www.youtube.com/watch?v={candidate}", cancellationToken);
            var ended = HasEnded(watch);
            if (ended is null)
            {
                failure = $"YouTube did not return recognizable broadcast details for {candidate}";
                continue;
            }
            if (ended == false) return (candidate, null);
        }
        return (null, failure);
    }

    internal static string? LiveVideoOf(string? page)
    {
        if (page is null || HasEnded(page) != false) return null;
        try
        {
            using var response = ReadData(page, PlayerResponse);
            return response is not null && response.RootElement.TryGetProperty("videoDetails", out var details) &&
                details.TryGetProperty("videoId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: 11 } video && video.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? video : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether a YouTube broadcast that was on air has been ended: true when its page says it is a
    /// live that is no longer live, false while it is live, null when the page could not be read
    /// or said neither. Only true is an answer to act on.
    /// </summary>
    public async Task<bool?> HasEndedAsync(string videoId, CancellationToken cancellationToken)
    {
        var watch = await ReadAsync($"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}", cancellationToken);
        return HasEnded(watch);
    }

    /// <summary>The reading of <see cref="HasEndedAsync"/>, on a page already read.</summary>
    internal static bool? HasEnded(string? watch)
    {
        if (watch is null) return null;
        try
        {
            using var response = ReadData(watch, PlayerResponse);
            if (response is null) return null;
            var details = response.RootElement.GetRawText();
            // Related videos in ytInitialData are not evidence about this broadcast.
            return OnAirNow.IsMatch(details) ? false : LiveContent.IsMatch(details) ? true : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? CandidateOf(string html) => CandidatesOf(html)?.FirstOrDefault();

    /// <summary>Null means unreadable; an empty recognized grid means no public broadcasts.</summary>
    internal static IReadOnlyList<string>? CandidatesOf(string html)
    {
        try
        {
            using var document = ReadData(html, InitialData);
            if (document is null) return null;
            var candidates = new List<(string Id, bool Live)>();
            Visit(document.RootElement);
            if (candidates.Count == 0 && (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("contents", out _))) return null;
            return candidates.OrderByDescending(entry => entry.Live).Select(entry => entry.Id).Distinct().ToArray();

            void Visit(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in node.EnumerateArray()) Visit(child);
                }
                else if (node.ValueKind == JsonValueKind.Object)
                {
                    // A selected channel tab contains the streams grid. Ignore recommendations
                    // and videos in unrelated tabs, which can belong to other channels.
                    if (node.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tab in tabs.EnumerateArray())
                            if (tab.TryGetProperty("tabRenderer", out var renderer) &&
                                renderer.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True &&
                                renderer.TryGetProperty("content", out var content)) Visit(content);
                        return;
                    }
                    if (node.TryGetProperty("contentType", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "LOCKUP_CONTENT_TYPE_VIDEO" &&
                        node.TryGetProperty("contentId", out var id)) Add(id, node);
                    else if (node.TryGetProperty("videoId", out var video) && node.TryGetProperty("thumbnail", out _)) Add(video, node);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (property.Name is "secondaryContents" or "header" or "topbar") continue;
                        Visit(property.Value);
                    }
                }
            }

            void Add(JsonElement id, JsonElement node)
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: 11 } value &&
                    value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                    candidates.Add((value, Regex.IsMatch(node.GetRawText(), @"""(?:style|overlayStyle|badgeStyle)""\s*:\s*""(?:LIVE|BADGE_STYLE_TYPE_LIVE_NOW|THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE)""")));
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page at an address, null when it could not be read at all. An empty page and a failed
    /// request are not the same answer: the first is a page that was read and had nothing on it,
    /// the second is a question this application got no answer to, and only the second is worth a
    /// page being told about.
    /// </summary>
    private async Task<string?> ReadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The platform page {Url} answered {Status}.", url, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client gave up on the platform, not the page: nothing to ask, nothing to answer.
            _logger.LogWarning("The platform page {Url} did not answer in time.", url);
            return null;
        }
        catch (HttpRequestException problem)
        {
            _logger.LogWarning(problem, "The platform page {Url} could not be read.", url);
            return null;
        }
    }
}
