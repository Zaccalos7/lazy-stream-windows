using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Whether YouTube is showing the lives it is being sent.
/// <para>An ingest that takes the stream is not a broadcast on air. YouTube keeps the two apart:
/// the stream key receives the RTMP, and Studio rates it "excellent", while the broadcast the
/// viewers watch has a life of its own. With Auto-start off it waits in preview for someone to
/// press Go live; with Auto-stop on, a connection that dropped for a few seconds ends it, and the
/// stream that comes back goes to no broadcast at all, or to a new one with a new id. In every case
/// this application is told the publish went well and nobody can see the live.</para>
/// <para>So every live going to YouTube is looked for where a viewer would look for it, the public
/// page of the streams of its channel, once it has been on air long enough for YouTube to have
/// put it there. A live that is not found there twice in a row is flagged, the pages draw the
/// flag as a warning, and the flag goes away the first time the live is found. A page that could
/// not be read says nothing either way: the platform being unreachable is not the live being off
/// air.</para>
/// </summary>
public sealed class YouTubeAirWatch : BackgroundService
{
    /// <summary>
    /// How long a live is on air before YouTube is expected to show it. Auto-start takes some
    /// seconds to move a broadcast from preview to live, and the page of the streams some more to
    /// list it: asking sooner would warn about every live that is simply starting.
    /// </summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);

    /// <summary>How often the lives are looked for. The lookup answers are held for longer anyway.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>How many answers in a row say "not on air" before the live is flagged.</summary>
    internal const int Misses = 2;

    private readonly StreamingSessionRegistry _sessions;
    private readonly VideoRepository _videos;
    private readonly VideoLiveHistoryRepository _histories;
    private readonly LivePlatformEmbeds _embeds;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<YouTubeAirWatch> _logger;

    /// <summary>The answers in a row that said "not on air", by the video a session streams.</summary>
    private readonly ConcurrentDictionary<int, int> _misses = new();

    /// <summary>The playlist or canvas a flagged video is part of, so its row can be told too.</summary>
    private readonly ConcurrentDictionary<int, long> _historyOf = new();

    public YouTubeAirWatch(
        StreamingSessionRegistry sessions,
        VideoRepository videos,
        VideoLiveHistoryRepository histories,
        LivePlatformEmbeds embeds,
        LiveChangeNotifier notifier,
        ILogger<YouTubeAirWatch> logger)
    {
        _sessions = sessions;
        _videos = videos;
        _histories = histories;
        _embeds = embeds;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Whether the live of a video is being sent to YouTube and YouTube is not showing it.</summary>
    public bool IsOffAir(int videoPkid) => _misses.TryGetValue(videoPkid, out var misses) && misses >= Misses;

    /// <summary>
    /// The same question for a row of the live page, which stands for a whole playlist or canvas:
    /// the row is flagged when any of the videos of its history is.
    /// </summary>
    public bool IsOffAir(int videoPkid, long? historyPkid) =>
        IsOffAir(videoPkid)
        || historyPkid is { } history && _historyOf.Any(entry => entry.Value == history && IsOffAir(entry.Key));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception problem) when (problem is not OperationCanceledException)
                {
                    _logger.LogWarning(problem, "The lives on YouTube could not be checked.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The application is closing.
        }
    }

    /// <summary>One pass over the running lives. The rows are redrawn when a flag changed.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var running = _sessions.Running.Where(session => !session.HasExited).ToList();
        var changed = false;

        foreach (var pkid in _misses.Keys.Where(pkid => running.All(session => session.VideoPkid != pkid)).ToList())
        {
            changed |= Forget(pkid);
        }

        foreach (var session in running)
        {
            if (session.Profile.Platform != StreamPlatform.YouTube)
            {
                continue;
            }

            // A live that is not on air yet, or has just come back after a reconnect, is given the
            // time YouTube needs before it is looked for.
            if (!session.IsOnAir || session.OnAirFor < Grace)
            {
                changed |= Forget(session.VideoPkid);
                continue;
            }

            var video = _videos.FindByPkid(session.VideoPkid);
            var history = video?.VideoLiveHistoryId is { } historyId ? _histories.FindByPkid(historyId) : null;
            if (video is null || history is null)
            {
                continue;
            }

            _historyOf[session.VideoPkid] = history.Pkid;
            var found = await _embeds.ResolveAsync(
                history.StreamUrl, video.ChannelName, history.PlatformStreamName, null, cancellationToken)
                .ConfigureAwait(false);
            changed |= Observe(session.VideoPkid, found);
        }

        if (changed)
        {
            _notifier.Raise();
        }
    }

    /// <summary>
    /// Takes one answer about a live: found clears it, "not on air" counts one more miss, and a
    /// page that could not be read leaves it as it was. True when the flag changed.
    /// </summary>
    internal bool Observe(int videoPkid, LivePlatformEmbeds.LivePlatformLookup found)
    {
        var before = IsOffAir(videoPkid);
        if (found.Embed is not null)
        {
            _misses.TryRemove(videoPkid, out _);
        }
        else if (found.Reason is null)
        {
            _misses.AddOrUpdate(videoPkid, 1, static (_, misses) => misses + 1);
        }

        var after = IsOffAir(videoPkid);
        if (before != after)
        {
            if (after)
            {
                _logger.LogWarning(
                    "The live of the video {Video} is being sent to YouTube but the channel has no public live.", videoPkid);
            }
            else
            {
                _logger.LogInformation("The live of the video {Video} is on air on YouTube.", videoPkid);
            }
        }

        return before != after;
    }

    /// <summary>Drops what is known about a live. True when it was flagged.</summary>
    internal bool Forget(int videoPkid)
    {
        var before = IsOffAir(videoPkid);
        _misses.TryRemove(videoPkid, out _);
        _historyOf.TryRemove(videoPkid, out _);
        return before;
    }
}
