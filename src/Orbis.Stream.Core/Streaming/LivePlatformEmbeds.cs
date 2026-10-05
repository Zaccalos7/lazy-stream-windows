using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Pages;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The player of a platform, for a page that wants to show the live as the viewers see it rather
/// than as the encoder sees it: the platform it goes on, the channel of the configuration, and the
/// address of the player that plays it.
/// </summary>
public sealed record LivePlatformEmbed(string Platform, string Channel, string Url);

/// <summary>
/// Where a live can be watched when the live is not only the local picture: the Twitch and the
/// YouTube player, addressed by the channel of the configuration.
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

    /// <summary>
    /// The first entry of the grid of a page of streams, which is the live when the channel is on
    /// air and the last broadcast when it is not. The pair of fields is what keeps the id of a
    /// video somewhere else on the page (the watch later list, the suggestions) from being taken for
    /// it.
    /// </summary>
    private static readonly Regex Lockup = new(
        "\"contentId\":\"(?<id>[A-Za-z0-9_-]{11})\",\"contentType\":\"LOCKUP_CONTENT_TYPE_VIDEO\"",
        RegexOptions.CultureInvariant);

    /// <summary>The same entry as the older page of a channel laid it out, with its thumbnail.</summary>
    private static readonly Regex Legacy = new(
        "\"videoId\":\"(?<id>[A-Za-z0-9_-]{11})\",\"thumbnail\"",
        RegexOptions.CultureInvariant);

    /// <summary>The data of a page of a channel, which is where the grid of its streams is.</summary>
    private static readonly Regex InitialData = new(
        @"var ytInitialData = (?<json>.*?);</script>",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// What YouTube says about a video, before it localises anything on the page: the details of
    /// the video say <c>isLive</c> and those of the broadcast <c>isLiveNow</c>, and both only while
    /// it is on air. Not <c>isLiveContent</c>, which a broadcast keeps once it has ended: it is the
    /// field that makes the last live of a channel off air look like a live.
    /// </summary>
    private static readonly Regex OnAirNow = new(
        "\"isLive(?:Now)?\":true",
        RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly ILogger<LivePlatformEmbeds> _logger;
    private readonly ConcurrentDictionary<string, (string? VideoId, DateTimeOffset Asked)> _youtube =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One question at a time: a page asking again while the first one is still open.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LivePlatformEmbeds(HttpClient http, ILogger<LivePlatformEmbeds> logger)
    {
        _http = http;
        _logger = logger;
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
        CancellationToken cancellationToken)
    {
        var channel = (channelName ?? platformStreamName)?.Trim();
        if (string.IsNullOrEmpty(channel))
        {
            return LivePlatformLookup.Nothing;
        }

        return LiveLinkView.PlatformOf(streamUrl) switch
        {
            "twitch" => LivePlatformLookup.Of(new LivePlatformEmbed("twitch", channel, TwitchUrl(channel, host))),
            "youtube" => await YouTubeAsync(channel, cancellationToken),
            _ => LivePlatformLookup.Nothing
        };
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
            $"https://www.youtube.com/embed/{videoId}?autoplay=1&mute=1&playsinline=1&rel=0&modestbranding=1"));
    }

    /// <summary>
    /// The id of the video a channel is broadcasting right now, or nothing. <c>Failure</c> is what
    /// could not be asked, as opposed to what was asked and found empty.
    /// </summary>
    private async Task<(string? VideoId, string? Failure)> LiveVideoAsync(string handle, CancellationToken cancellationToken)
    {
        if (Remembered(handle) is { } fresh)
        {
            return (fresh, null);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Remembered(handle) is { } again)
            {
                return (again, null);
            }

            var (videoId, failure) = await LookUpAsync(handle, cancellationToken);
            _youtube[handle] = (videoId, DateTimeOffset.UtcNow);
            return (videoId, failure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception problem)
        {
            // A platform that cannot be asked is not a reason to fail the preview: the answer the
            // page already had is the one it keeps.
            _logger.LogWarning(problem, "The live of the YouTube channel {Channel} could not be looked up.", handle);
            return _youtube.TryGetValue(handle, out var last)
                ? (last.VideoId, null)
                : (null, "YouTube could not be asked for the streams of the channel");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The answer for a channel that was asked recently enough to still be a good one.</summary>
    private string? Remembered(string handle)
    {
        if (!_youtube.TryGetValue(handle, out var entry))
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - entry.Asked;
        return age < (entry.VideoId is null ? OffAir : OnAir) ? entry.VideoId : null;
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
        if (streams is null)
        {
            // Not "the channel is off air": the page never arrived. A handle that is not a handle
            // and a platform that answered a request from this machine with something else both end
            // here, and both have to be told apart from a channel that is simply not broadcasting.
            return (null, $"YouTube did not return the streams page of @{handle}");
        }

        var candidate = CandidateOf(streams);
        if (candidate is null)
        {
            return (null, null);
        }

        var watch = await ReadAsync($"https://www.youtube.com/watch?v={candidate}", cancellationToken);
        if (watch is null)
        {
            return (null, $"YouTube did not return the page of the video {candidate}");
        }

        return OnAirNow.IsMatch(watch) ? (candidate, null) : (null, null);
    }

    /// <summary>The id of the first entry of the grid of a page of a channel, or nothing.</summary>
    internal static string? CandidateOf(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        // Only the data of the page is read, and not the scripts around it: the id of a video
        // elsewhere on the page is not the top of the grid, and the grid is what is wanted.
        var data = InitialData.Match(html);
        var scope = data.Success ? data.Groups["json"].Value : html;

        var entry = Lockup.Match(scope);
        if (!entry.Success)
        {
            entry = Legacy.Match(scope);
        }

        return entry.Success ? entry.Groups["id"].Value : null;
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