using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Whether YouTube is showing the lives it is being sent, and whether it has ended one of them.
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
/// <para>A live that was found on air and then ended in Studio is stopped here too, the way the
/// user would stop it, because a stream nobody can see is only an encoder working for nothing.
/// That is the one thing this class does to a live, so it asks for more than an absence: the
/// channel has no live any more, and the page of the very video that was on air says it is a live
/// that is over - twice in a row. A page this application cannot read, or reads and does not
/// understand, is never an end; and a broadcast YouTube replaced with a new one for the same
/// stream is found again on the channel, which keeps the live going.</para>
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

    private readonly StreamingSessionRegistry _sessions;
    private readonly VideoRepository _videos;
    private readonly VideoLiveHistoryRepository _histories;
    private readonly LivePlatformEmbeds _embeds;
    private readonly StreamingService _streaming;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<YouTubeAirWatch> _logger;

    /// <summary>What is known of each live, by the video its session streams.</summary>
    private readonly YouTubeAirState _state = new();

    public YouTubeAirWatch(
        StreamingSessionRegistry sessions,
        VideoRepository videos,
        VideoLiveHistoryRepository histories,
        LivePlatformEmbeds embeds,
        StreamingService streaming,
        Localizer localizer,
        LiveChangeNotifier notifier,
        ILogger<YouTubeAirWatch> logger)
    {
        _sessions = sessions;
        _videos = videos;
        _histories = histories;
        _embeds = embeds;
        _streaming = streaming;
        _localizer = localizer;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Whether the live of a video is being sent to YouTube and YouTube is not showing it.</summary>
    public bool IsOffAir(int videoPkid) => _state.IsOffAir(videoPkid);

    /// <summary>
    /// The same question for a row of the live page, which stands for a whole playlist or canvas:
    /// the row is flagged when any of the videos of its history is.
    /// </summary>
    public bool IsOffAir(int videoPkid, long? historyPkid) => _state.IsOffAir(videoPkid, historyPkid);

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

        foreach (var pkid in _state.Known.Where(pkid => running.All(session => session.VideoPkid != pkid)).ToList())
        {
            changed |= _state.Forget(pkid);
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
                changed |= _state.Forget(session.VideoPkid);
                continue;
            }

            var video = _videos.FindByPkid(session.VideoPkid);
            var history = video?.VideoLiveHistoryId is { } historyId ? _histories.FindByPkid(historyId) : null;
            if (video is null || history is null)
            {
                continue;
            }

            _state.BelongsTo(session.VideoPkid, history.Pkid);
            var found = await _embeds.ResolveAsync(
                history.StreamUrl, video.ChannelName, history.PlatformStreamName, null, cancellationToken)
                .ConfigureAwait(false);
            if (_state.Observe(session.VideoPkid, found))
            {
                changed = true;
                if (_state.IsOffAir(session.VideoPkid))
                {
                    _logger.LogWarning(
                        "The live of the video {Video} is being sent to YouTube but the channel has no public live.", session.VideoPkid);
                }
                else
                {
                    _logger.LogInformation("The live of the video {Video} is on air on YouTube.", session.VideoPkid);
                }
            }

            // Not on the channel any more, after having been there: is the broadcast that was on
            // air over? Only that page answers it.
            if (found is { Embed: null, Reason: null } && _state.OnAirVideoOf(session.VideoPkid) is { } videoId)
            {
                var ended = await _embeds.HasEndedAsync(videoId, cancellationToken).ConfigureAwait(false);
                if (_state.ObserveEnding(session.VideoPkid, ended))
                {
                    Stop(session.VideoPkid, videoId);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            _notifier.Raise();
        }
    }

    private void Stop(int videoPkid, string videoId)
    {
        _logger.LogWarning(
            "The broadcast {Broadcast} of the video {Video} was ended on YouTube: the live is stopped here too.",
            videoId,
            videoPkid);
        _state.Forget(videoPkid);
        try
        {
            _streaming.StopBecause(videoPkid, _localizer.PrintMessage("live.stopped.youtube"));
        }
        catch (Exception problem)
        {
            // The live may have ended on its own in the meantime: there is nothing left to stop.
            _logger.LogWarning(problem, "The live of the video {Video} could not be stopped.", videoPkid);
        }
    }
}
