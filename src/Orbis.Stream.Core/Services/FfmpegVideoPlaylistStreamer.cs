using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Port of the frame loop of <c>StreamService#startVideoStreaming</c>: it streams the videos of a
/// live history one after another, mirrors every state change on the video row and honours the
/// "should be stopped" flag that <c>/live/stop-live</c> sets. A video that was interrupted keeps
/// the position it was stopped at, so playing it again carries on from there instead of over.
/// </summary>
public sealed class FfmpegVideoPlaylistStreamer : IVideoPlaylistStreamer
{
    private static readonly TimeSpan StopFlagPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How close to the end a video has to be to count as streamed through.</summary>
    private const int FinishedToleranceMilliseconds = 250;

    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly FfmpegProbe _probe;
    private readonly FfmpegToolLocator _locator;
    private readonly StreamingSessionRegistry _sessions;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<FfmpegVideoPlaylistStreamer> _logger;

    public FfmpegVideoPlaylistStreamer(
        VideoRepository videoRepository,
        VideoSettingRepository videoSettingRepository,
        FfmpegProbe probe,
        FfmpegToolLocator locator,
        StreamingSessionRegistry sessions,
        Localizer localizer,
        LiveChangeNotifier notifier,
        ILogger<FfmpegVideoPlaylistStreamer> logger)
    {
        _videoRepository = videoRepository;
        _videoSettingRepository = videoSettingRepository;
        _probe = probe;
        _locator = locator;
        _sessions = sessions;
        _localizer = localizer;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
        var queue = await PlanAsync(videos, cancellationToken).ConfigureAwait(false);

        if (queue.Count == 0)
        {
            // Nothing is left of the pass that was interrupted: this is a play on a playlist that
            // ran to the end, so the videos start over instead of leaving the page with nothing
            // to watch. (The restart button asks for the same thing without waiting for the end.)
            _logger.LogInformation(
                "{Message}", _localizer.PrintMessage("live.restarted.from.beginning", [videos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            ForgetPositions(videos);
            queue = [.. videos.Select(video => new PlannedVideo(video, null, TimeSpan.Zero))];
        }

        foreach (var planned in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StreamVideoAsync(planned, outputUrl, videoLiveHistoryPkid, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What is left to stream, in playlist order. A video that was already streamed through is
    /// left out, a video that was interrupted is asked for its own position: only those two need to
    /// know how long they are, so a first play probes nothing before the loop starts.
    /// </summary>
    private async Task<IReadOnlyList<PlannedVideo>> PlanAsync(
        IReadOnlyList<VideoEntity> videos,
        CancellationToken cancellationToken)
    {
        var queue = new List<PlannedVideo>();

        foreach (var video in videos)
        {
            if (video.LastTimeStampBeforeStop <= 0)
            {
                queue.Add(new PlannedVideo(video, null, TimeSpan.Zero));
                continue;
            }

            var probe = await _probe.ProbeAsync(video.VideoPath, cancellationToken).ConfigureAwait(false);
            if (IsStreamedThrough(video, probe))
            {
                continue;
            }

            queue.Add(new PlannedVideo(video, probe, TimeSpan.FromMilliseconds(video.LastTimeStampBeforeStop)));
        }

        return queue;
    }

    /// <summary>Position recorded and position in the file: the first of the two is the whole file.</summary>
    private static bool IsStreamedThrough(VideoEntity video, MediaProbeResult probe)
    {
        if (video.LiveStatus == LiveStatus.Ended)
        {
            return true;
        }

        return probe.DurationSeconds > 0
            && video.LastTimeStampBeforeStop >= (long)(probe.DurationSeconds * 1000) - FinishedToleranceMilliseconds;
    }

    /// <summary>The rows go back to "never streamed", which is what the restart button means.</summary>
    private void ForgetPositions(IReadOnlyList<VideoEntity> videos)
    {
        foreach (var video in videos)
        {
            if (video.LastTimeStampBeforeStop == 0)
            {
                continue;
            }

            video.LastTimeStampBeforeStop = 0;
            _videoRepository.Update(video);
        }

        _notifier.Raise();
    }

    private async Task StreamVideoAsync(
        PlannedVideo planned,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
        var video = planned.Video;
        var inputPath = video.VideoPath;
        var videoKey = video.Pkid;

        try
        {
            var videoSetting = video.VideoSettingId is null
                ? null
                : _videoSettingRepository.FindById(video.VideoSettingId.Value);

            if (videoSetting is null)
            {
                throw new InvalidOperationException(
                    $"video setting not found for video {videoKey} (videoSettingId={video.VideoSettingId?.ToString() ?? "null"})");
            }

            _logger.LogInformation("INPUT: working directory = {InputPath}", inputPath);

            var probe = planned.Probe ?? await _probe.ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "FORMAT: of grabber on running machine = {PixelFormat}", FfmpegCodecCatalog.ResolvePixelFormat(videoSetting.PixelFormat));

            if (planned.ResumeFrom > TimeSpan.Zero)
            {
                var resumed = _localizer.PrintMessage("video.live.resumed", [inputPath, Seconds(planned.ResumeFrom)]);
                _logger.LogInformation("{Message}", resumed);
            }

            await using var session = FfmpegStreamingSession.Start(
                _locator, videoKey, inputPath, outputUrl, videoSetting, probe, _logger, planned.ResumeFrom);

            _sessions.Register(session);

            var startedMessage = _localizer.PrintMessage("video.live.started", [inputPath]);
            _logger.LogInformation("{Message}", startedMessage);
            SaveMessageOnVideoLiveHistory(
                startedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Live, DateTime.Now);

            var wasStopped = await MonitorAsync(session, videoKey, videoLiveHistoryPkid, inputPath, cancellationToken)
                .ConfigureAwait(false);

            if (wasStopped)
            {
                return;
            }

            var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);

            if (exitCode != 0 && _videoRepository.FindByPkid(videoKey)?.ShouldBeStop == true)
            {
                await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
                return;
            }

            if (exitCode == 0)
            {
                var endedMessage = _localizer.PrintMessage("video.live.ended");
                _logger.LogInformation("{Message}", endedMessage);
                SaveMessageOnVideoLiveHistory(
                    endedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Ended, null, EndOf(probe, session));
                return;
            }

            var streamingError = _localizer.PrintMessage(
                "error.during.streaming.video", [inputPath, videoLiveHistoryPkid])
                + "\n"
                + (string.IsNullOrWhiteSpace(errorOutput) ? $"ffmpeg exited with code {exitCode}" : errorOutput.Trim());
            _logger.LogError("{Message}", streamingError);
            SaveMessageOnVideoLiveHistory(
                streamingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var startingError = _localizer.PrintMessage("error.during.starting.video", [inputPath])
                + "\n"
                + exception.Message;
            _logger.LogError("{Message}", startingError);
            SaveMessageOnVideoLiveHistory(startingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now, 0);
        }
        finally
        {
            _sessions.Remove(videoKey);
        }
    }

    /// <summary>
    /// Polls the stop flag like the Java version polled it every 50 frames, but also reacts
    /// immediately to the in-memory stop signal.
    /// </summary>
    private async Task<bool> MonitorAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (session.StopRequested)
            {
                return await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
            }

            var video = _videoRepository.FindByPkid(videoKey);
            if (video is null)
            {
                _logger.LogWarning(
                    "{Message}", _localizer.PrintMessage("error.during.retrieved.video", [inputPath, videoLiveHistoryPkid]));
                return false;
            }

            if (video.ShouldBeStop)
            {
                return await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(StopFlagPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (session.HasExited)
            {
                return false;
            }
        }
    }

    private async Task<bool> StopAndRecordAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath)
    {
        await session.StopAsync().ConfigureAwait(false);

        var message = _localizer.PrintMessage("live.stopped");
        _logger.LogInformation("{Message}", message);
        _videoRepository.SetStopFlag(videoKey, false);
        SaveMessageOnVideoLiveHistory(
            message, videoLiveHistoryPkid, inputPath, LiveStatus.Stopped, null, session.PositionMilliseconds);
        return true;
    }

    /// <summary>
    /// The end of the file when it is known: a video that reached it is not streamed again, and the
    /// position it stopped at is the last one a play could resume from.
    /// </summary>
    private static long EndOf(MediaProbeResult probe, FfmpegStreamingSession session) => Math.Max(
        session.PositionMilliseconds,
        probe.DurationSeconds > 0 ? (long)(probe.DurationSeconds * 1000) : 0);

    private static string Seconds(TimeSpan position) =>
        position.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Port of <c>saveMessageOnVideoLiveHistory</c>.</summary>
    private void SaveMessageOnVideoLiveHistory(
        string message,
        long videoLiveHistoryPkid,
        string inputPath,
        LiveStatus liveStatus,
        DateTime? dateTime,
        long? positionMilliseconds = null)
    {
        var video = _videoRepository.FindByVideoPathAndVideoLiveHistoryPkid(inputPath, videoLiveHistoryPkid);
        if (video is null)
        {
            _logger.LogError(
                "{Message}", _localizer.PrintMessage("error.during.retrieved.video", [inputPath, videoLiveHistoryPkid]));
            return;
        }

        if (dateTime is not null)
        {
            video.StartDateLive = dateTime;
        }

        if (positionMilliseconds is { } position)
        {
            video.LastTimeStampBeforeStop = Math.Max(0, position);
        }

        video.Message = message;
        video.LiveStatus = liveStatus;
        _videoRepository.Update(video);

        // This is the only place where the engine changes what a row shows, so it is also the
        // only place that has to tell the open pages that the row they drew is out of date.
        _notifier.Raise();
    }

    /// <summary>A video of the playlist, with what is known about it before ffmpeg is started.</summary>
    private sealed record PlannedVideo(VideoEntity Video, MediaProbeResult? Probe, TimeSpan ResumeFrom);
}

/// <summary>Abstraction of the playlist streamer, so the services can be unit tested without ffmpeg.</summary>
public interface IVideoPlaylistStreamer
{
    Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken);
}
