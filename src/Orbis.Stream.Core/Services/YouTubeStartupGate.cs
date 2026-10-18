using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Sends only a static picture and silence until YouTube exposes a live broadcast. The actual
/// files and capture devices are opened by the playlist streamer only after this gate succeeds.
/// A retry replaces the RTMP connection, never the user's event or its saved playback position.
/// </summary>
public sealed class YouTubeStartupGate
{
    internal const int Attempts = 3;
    private readonly LivePlatformEmbeds _embeds;
    private readonly StreamingSessionRegistry _sessions;
    private readonly FfmpegToolLocator _locator;
    private readonly Localizer _localizer;
    private readonly ILogger<YouTubeStartupGate> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _retryDelay;

    public YouTubeStartupGate(LivePlatformEmbeds embeds, StreamingSessionRegistry sessions,
        FfmpegToolLocator locator, Localizer localizer, ILogger<YouTubeStartupGate> logger)
        : this(embeds, sessions, locator, localizer, logger,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)) { }

    internal YouTubeStartupGate(LivePlatformEmbeds embeds, StreamingSessionRegistry sessions,
        FfmpegToolLocator locator, Localizer localizer, ILogger<YouTubeStartupGate> logger,
        TimeSpan timeout, TimeSpan poll, TimeSpan retryDelay)
    {
        _embeds = embeds;
        _sessions = sessions;
        _locator = locator;
        _localizer = localizer;
        _logger = logger;
        _timeout = timeout;
        _poll = poll;
        _retryDelay = retryDelay;
    }

    public async Task<bool> WaitAsync(int videoPkid, string channel, LiveOutput output,
        VideoSettingEntity setting, MediaOutput frame, TimeSpan resumeFrom,
        Func<bool> stopped, Action<LiveStatus, string> report, CancellationToken cancellationToken)
    {
        if (output.Profile.Platform != StreamPlatform.YouTube || output.BroadcastVerified) return true;
        string? reason = null;
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopped()) return Stop();
                output.Abort();
                await _embeds.RefreshYouTubeAsync(channel, cancellationToken).ConfigureAwait(false);
                var waiting = _localizer.PrintMessage("video.live.youtube.waiting", [attempt, Attempts]);
                report(attempt == 1 ? LiveStatus.Connecting : LiveStatus.Reconnecting, waiting);
                _logger.LogInformation("{Message}", waiting);

                FfmpegStreamingSession? standby = null;
                var confirmed = false;
                try
                {
                    standby = FfmpegStreamingSession.StartStandby(_locator, videoPkid, setting, frame,
                        output, _logger, resumeFrom);
                    _sessions.Register(standby);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_timeout);
                    var nextCheck = Stopwatch.GetTimestamp();
                    while (!timeout.IsCancellationRequested)
                    {
                        if (standby.StopRequested || stopped()) return Stop();
                        if (standby.HasExited) break;
                        if (standby.IsPublishing && Stopwatch.GetTimestamp() >= nextCheck)
                        {
                            var found = await _embeds.ResolveYouTubeChannelAsync(channel, timeout.Token).ConfigureAwait(false);
                            if (standby.StopRequested || stopped()) return Stop();
                            if (found.Embed?.VideoId is not null && standby.IsPublishing)
                            {
                                // Keep this publish open. The program starts on its own timeline
                                // after the queued standby frames, with no seek past unseen content.
                                await standby.HandOverAsync().ConfigureAwait(false);
                                output.ConfirmBroadcast();
                                confirmed = true;
                                _logger.LogInformation("YouTube broadcast {Video} confirmed on attempt {Attempt}; starting the program at {Position} ms.",
                                    found.Embed.VideoId, attempt, resumeFrom.TotalMilliseconds);
                                return true;
                            }
                            reason = found.Reason ?? "No public broadcast found on the YouTube channel";
                            nextCheck = Stopwatch.GetTimestamp() + (long)(_poll.TotalSeconds * Stopwatch.Frequency);
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // This attempt timed out. The program has still not been opened.
                }
                catch (Exception problem) when (problem is not OperationCanceledException)
                {
                    reason = FfmpegStreamingSession.RedactStreamKey(problem.Message, output.OutputUrl);
                    _logger.LogWarning("YouTube startup attempt {Attempt} failed: {Reason}", attempt, reason);
                }
                finally
                {
                    if (!confirmed) output.Abort();
                    if (standby is not null)
                    {
                        if (_sessions.TryGet(videoPkid, out var current) && ReferenceEquals(current, standby))
                            _sessions.Remove(videoPkid);
                        await standby.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (stopped()) return Stop();
                if (attempt < Attempts)
                {
                    _logger.LogWarning("YouTube has not confirmed attempt {Attempt}/{Attempts}: {Reason}. The program remains paused; opening a new publish next.",
                        attempt, Attempts, reason);
                    var until = Stopwatch.GetTimestamp() + (long)(_retryDelay.TotalSeconds * attempt * Stopwatch.Frequency);
                    while (Stopwatch.GetTimestamp() < until)
                    {
                        if (stopped()) return Stop();
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            var message = _localizer.PrintMessage("video.live.youtube.failed", [Attempts]);
            if (!string.IsNullOrWhiteSpace(reason)) message += "\n" + reason;
            report(LiveStatus.Error, message);
            _logger.LogWarning("{Message}", message);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.Abort();
            report(LiveStatus.Stopped, _localizer.PrintMessage("live.stopped"));
            throw;
        }

        bool Stop()
        {
            output.Abort();
            report(LiveStatus.Stopped, _localizer.PrintMessage("live.stopped"));
            return false;
        }
    }
}
