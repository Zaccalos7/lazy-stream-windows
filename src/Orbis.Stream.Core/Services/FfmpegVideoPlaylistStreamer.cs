using System.Globalization;
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

    /// <summary>
    /// What a canvas is captured at when nothing else says: gdigrab reads 5 frames a second unless
    /// it is told otherwise, and a live at 5 frames a second is not a watchable stream.
    /// </summary>
    private const double DefaultCanvasFrameRate = 30d;

    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly SceneRepository _sceneRepository;
    private readonly FfmpegProbe _probe;
    private readonly FfmpegToolLocator _locator;
    private readonly StreamingSessionRegistry _sessions;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly LivePreviewFrames _frames;
    private readonly ILogger<FfmpegVideoPlaylistStreamer> _logger;

    public FfmpegVideoPlaylistStreamer(
        VideoRepository videoRepository,
        VideoSettingRepository videoSettingRepository,
        SceneRepository sceneRepository,
        FfmpegProbe probe,
        FfmpegToolLocator locator,
        StreamingSessionRegistry sessions,
        Localizer localizer,
        LiveChangeNotifier notifier,
        LivePreviewFrames frames,
        ILogger<FfmpegVideoPlaylistStreamer> logger)
    {
        _videoRepository = videoRepository;
        _videoSettingRepository = videoSettingRepository;
        _sceneRepository = sceneRepository;
        _probe = probe;
        _locator = locator;
        _sessions = sessions;
        _localizer = localizer;
        _notifier = notifier;
        _frames = frames;
        _logger = logger;
    }

    public async Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
        // Rows that came from a canvas are not a playlist: they are one picture, so they are grouped
        // and streamed as a single ffmpeg instead of one after the other.
        var groups = GroupAsync(videos).ToList();
        var files = groups.Where(group => group.ScenePkid is null).SelectMany(group => group.Videos).ToList();

        if (files.Count == 0)
        {
            foreach (var scene in groups.Where(group => group.ScenePkid is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await StreamSceneAsync(scene.Videos, outputUrl, videoLiveHistoryPkid, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var queue = await PlanAsync(files, cancellationToken).ConfigureAwait(false);

        if (queue.Count == 0)
        {
            // Nothing is left of the pass that was interrupted: this is a play on a playlist that
            // ran to the end, so the videos start over instead of leaving the page with nothing
            // to watch. (The restart button asks for the same thing without waiting for the end.)
            _logger.LogInformation(
                "{Message}", _localizer.PrintMessage("live.restarted.from.beginning", [videos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
            ForgetPositions(files);
            queue = [.. files.Select(video => new PlannedVideo(video, null, TimeSpan.Zero))];
        }

        for (var i = 0; i < queue.Count; i++)
        {
            var planned = queue[i];
            cancellationToken.ThrowIfCancellationRequested();

            // A stop is for the whole playlist, not for the video that happened to be on air: the
            // next one must not go live by itself. It stays where it is, so a play resumes here.
            var stopped = await StreamVideoAsync(planned, outputUrl, videoLiveHistoryPkid, cancellationToken)
                .ConfigureAwait(false);
            if (stopped)
            {
                return;
            }

            // If this was the last video in the playlist and it ended (not stopped), mark the live as ended.
            if (i == queue.Count - 1)
            {
                var endedMessage = _localizer.PrintMessage("video.live.ended");
                _logger.LogInformation("{Message} - playlist complete", endedMessage);
                foreach (var video in files)
                {
                    video.LiveStatus = LiveStatus.Ended;
                    video.Message = endedMessage;
                    _videoRepository.Update(video);
                }
                _notifier.Raise();
                return;
            }
        }
    }

    /// <summary>
    /// Splits the rows in playlist order into one group per canvas plus one group for the files.
    /// A live from a folder has no canvas on any of its rows, so it comes back as it went in.
    /// </summary>
    private static IEnumerable<VideoGroup> GroupAsync(IReadOnlyList<VideoEntity> videos)
    {
        VideoGroup? scene = null;
        var files = new List<VideoEntity>();

        foreach (var video in videos)
        {
            if (video.ScenePkid is not { } scenePkid)
            {
                files.Add(video);
                scene = null;
                continue;
            }

            // A different canvas, or a file in between two of them, closes the group being built:
            // the rows of one canvas have to be contiguous to be composited together.
            if (scene is null || scene.ScenePkid != scenePkid)
            {
                if (scene is not null)
                {
                    yield return scene;
                }

                scene = new VideoGroup(scenePkid, []);
            }

            scene.Videos.Add(video);
        }

        if (scene is not null)
        {
            yield return scene;
        }

        if (files.Count > 0)
        {
            yield return new VideoGroup(null, files);
        }
    }

    /// <summary>What is left to stream, in playlist order. A video that was already streamed through is
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
            if (video.LastTimeStampBeforeStop == 0 && video.LiveStatus == LiveStatus.Offline)
            {
                continue;
            }

            // The status goes back with the position, for the same reason as the restart button.
            video.LastTimeStampBeforeStop = 0;
            video.LiveStatus = LiveStatus.Offline;
            _videoRepository.Update(video);
        }

        _notifier.Raise();
    }

    /// <returns>Whether the user stopped the live, as opposed to the video ending or failing.</returns>
    private async Task<bool> StreamVideoAsync(
        PlannedVideo planned,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
        var video = planned.Video;
        var inputPath = video.VideoPath;
        var videoKey = video.Pkid;

        // The session of the pass in progress, kept out of the <c>using</c> of an iteration so that
        // the new one is registered before the old one is killed. A preview that asks in between the
        // two has to see the live going on, not a moment where nothing is running.
        FfmpegStreamingSession? session = null;

        try
        {
            var probe = planned.Probe ?? await _probe.ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
            var resumeFrom = planned.ResumeFrom;

            // One pass per ffmpeg process. The pass ends when the file does, when the user stops it,
            // or when the encoder configuration changed and the only way to honour it is to start
            // again from where the transcode got to.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Read on every pass, not once before the loop: this is where a parameter changed
                // from the preview page arrives, and it has to reach the next command line.
                var videoSetting = FindSetting(videoKey);

                _logger.LogInformation("INPUT: working directory = {InputPath}", inputPath);
                _logger.LogInformation(
                    "FORMAT: of grabber on running machine = {PixelFormat}", FfmpegCodecCatalog.ResolvePixelFormat(videoSetting.PixelFormat));

                if (resumeFrom > TimeSpan.Zero)
                {
                    var resumed = _localizer.PrintMessage("video.live.resumed", [inputPath, Seconds(resumeFrom)]);
                    _logger.LogInformation("{Message}", resumed);
                }

                var next = FfmpegStreamingSession.Start(
                    _locator, videoKey, inputPath, outputUrl, videoSetting, probe, _logger, resumeFrom, _frames.PathOf(videoKey));

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                var startedMessage = _localizer.PrintMessage("video.live.started", [inputPath]);
                _logger.LogInformation("{Message}", startedMessage);
                SaveMessageOnVideoLiveHistory(
                    startedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Live, DateTime.Now);

                var outcome = await MonitorAsync(session, videoKey, videoLiveHistoryPkid, inputPath, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    return true;
                }

                if (outcome == StreamOutcome.Reconfigured)
                {
                    // Where the transcode got to is where the new one carries on from: the live
                    // skips nothing, it only pays for the second ffmpeg starting.
                    resumeFrom = TimeSpan.FromMilliseconds(session.PositionMilliseconds);
                    var reconfigured = _localizer.PrintMessage("video.live.reconfigured", [inputPath]);
                    _logger.LogInformation("{Message}", reconfigured);
                    SaveMessageOnVideoLiveHistory(
                        reconfigured, videoLiveHistoryPkid, inputPath, LiveStatus.Live, null);
                    continue;
                }

                var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);

                if (session.StopRequested || (exitCode != 0 && _videoRepository.FindByPkid(videoKey)?.ShouldBeStop == true))
                {
                    await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
                    return true;
                }

                if (exitCode == 0)
                {
                    var endedMessage = _localizer.PrintMessage("video.live.ended");
                    _logger.LogInformation("{Message}", endedMessage);
                    SaveMessageOnVideoLiveHistory(
                        endedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Ended, null, EndOf(probe, session));
                    return false;
                }

                var streamingError = _localizer.PrintMessage(
                    "error.during.streaming.video", [inputPath, videoLiveHistoryPkid])
                    + "\n"
                    + (string.IsNullOrWhiteSpace(errorOutput) ? $"ffmpeg exited with code {exitCode}" : errorOutput.Trim());
                _logger.LogError("{Message}", streamingError);
                SaveMessageOnVideoLiveHistory(
                    streamingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now, 0);
                return false;
            }
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
            return false;
        }
        finally
        {
            _sessions.Remove(videoKey);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _frames.Forget(videoKey);
        }
    }

    /// <summary>
    /// The files whose sound was asked for but that have none. A video without an audio track is
    /// common, and <c>[n:a]</c> on it fails the whole graph before the first frame: such a file
    /// stays on the canvas and simply leaves the mix. A file ffprobe cannot read counts as silent,
    /// so the error the user sees is ffmpeg's about the file, not a filter one about its sound.
    /// </summary>
    private async Task<HashSet<VideoEntity>> SilentFilesAsync(
        IReadOnlyList<VideoEntity> rows,
        CancellationToken cancellationToken)
    {
        var silent = new HashSet<VideoEntity>();
        foreach (var row in rows.Where(row => row.SourceKind == SourceKind.File && row.AudioEnabled))
        {
            try
            {
                var probe = await _probe.ProbeAsync(row.VideoPath, cancellationToken).ConfigureAwait(false);
                if (!probe.HasAudio)
                {
                    silent.Add(row);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not probe {Path}", row.VideoPath);
                silent.Add(row);
            }
        }

        return silent;
    }

    /// <summary>
    /// The output size is the one the canvas was drawn with. A scene deleted while its live was
    /// still in the history has no size any more, and then the box around the tiles is the
    /// smallest canvas that still holds every one of them where it was dropped.
    /// </summary>
    private (int Width, int Height) CanvasSizeOf(long? scenePkid, IReadOnlyList<VideoEntity> rows)
    {
        if (scenePkid is { } pkid
            && _sceneRepository.FindByPkid(pkid) is { Width: > 0, Height: > 0 } scene)
        {
            return (scene.Width!.Value, scene.Height!.Value);
        }

        var pictures = rows.Where(row => row.SourceKind.HasPicture()).ToList();
        return (
            pictures.Select(row => (row.X ?? 0) + (row.Width ?? 0)).DefaultIfEmpty(0).Max(),
            pictures.Select(row => (row.Y ?? 0) + (row.Height ?? 0)).DefaultIfEmpty(0).Max());
    }

    /// <summary>
    /// One ffmpeg for a whole canvas. The rows of a scene are its sources, not a playlist: they are
    /// opened together and laid over each other, and the pass lasts until the user stops it, because
    /// a capture device has no end to reach.
    /// </summary>
    private async Task StreamSceneAsync(
        IReadOnlyList<VideoEntity> rows,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
        var baseRow = rows.FirstOrDefault(row => row.SourceKind.HasPicture());
        if (baseRow is null)
        {
            return;
        }

        var (canvasWidth, canvasHeight) = CanvasSizeOf(baseRow.ScenePkid, rows);
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            var message = _localizer.PrintMessage("error.scene.no.canvas", [baseRow.Name]);
            _logger.LogError("{Message}", message);
            foreach (var row in rows)
            {
                SaveMessageOnVideoLiveHistory(
                    message, videoLiveHistoryPkid, row.VideoPath, LiveStatus.Error, DateTime.Now, 0);
            }

            return;
        }

        // If all sources are files (no screen/camera/microphone), the scene has a finite duration.
        // We should end the live when the longest file ends, not loop forever.
        var onlyFiles = rows.All(row => row.SourceKind == SourceKind.File);
        var videoKey = baseRow.Pkid;
        var description = _localizer.PrintMessage("video.live.scene", [rows.Count.ToString(CultureInfo.InvariantCulture)]);
        FfmpegStreamingSession? session = null;

        try
        {
            var resumeFrom = TimeSpan.Zero;
            var silent = await SilentFilesAsync(rows, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var videoSetting = FindSetting(videoKey);

                var items = rows
                    .Select(row => new FfmpegCompositionItem(
                        row.SourceKind,
                        row.SourceKind == SourceKind.File ? row.VideoPath : row.SourceTarget ?? string.Empty,
                        row.X ?? 0,
                        row.Y ?? 0,
                        row.Width ?? 0,
                        row.Height ?? 0,
                        row.AudioEnabled && !silent.Contains(row)))
                    .ToList();

                var next = FfmpegStreamingSession.StartComposition(
                    _locator,
                    videoKey,
                    items,
                    outputUrl,
                    videoSetting,
                    canvasWidth,
                    canvasHeight,
                    DefaultCanvasFrameRate,
                    _logger,
                    resumeFrom,
                    _frames.PathOf(videoKey));

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                MarkRows(rows, LiveStatus.Live, description);
                _logger.LogInformation("{Message}", description);

                var outcome = await MonitorAsync(session, videoKey, videoLiveHistoryPkid, baseRow.VideoPath, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    var stopped = _localizer.PrintMessage("live.stopped");
                    MarkRows(rows, LiveStatus.Stopped, stopped);
                    return;
                }

                if (outcome == StreamOutcome.Reconfigured)
                {
                    resumeFrom = TimeSpan.FromMilliseconds(session.PositionMilliseconds);
                    var reconfigured = _localizer.PrintMessage("video.live.reconfigured", [description]);
                    _logger.LogInformation("{Message}", reconfigured);
                    SaveMessageOnVideoLiveHistory(
                        reconfigured, videoLiveHistoryPkid, baseRow.VideoPath, LiveStatus.Live, null);
                    continue;
                }

                var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);

                if (session.StopRequested || (exitCode != 0 && _videoRepository.FindByPkid(videoKey)?.ShouldBeStop == true))
                {
                    var stopped = _localizer.PrintMessage("live.stopped");
                    MarkRows(rows, LiveStatus.Stopped, stopped);
                    return;
                }

                // If the scene has only file sources and ffmpeg exited cleanly, the video ended.
                // End the live instead of restarting the loop.
                if (onlyFiles && exitCode == 0)
                {
                    var endedMessage = _localizer.PrintMessage("video.live.ended");
                    _logger.LogInformation("{Message}", endedMessage);
                    MarkRows(rows, LiveStatus.Ended, endedMessage);
                    return;
                }

                var failure = _localizer.PrintMessage("error.during.streaming.video", [description, videoLiveHistoryPkid])
                    + "\n"
                    + (string.IsNullOrWhiteSpace(errorOutput) ? $"ffmpeg exited with code {exitCode}" : errorOutput.Trim());
                _logger.LogError("{Message}", failure);
                MarkRows(rows, LiveStatus.Error, failure);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = _localizer.PrintMessage("error.during.starting.video", [description]) + "\n" + exception.Message;
            _logger.LogError("{Message}", failure);
            MarkRows(rows, LiveStatus.Error, failure);
        }
        finally
        {
            _sessions.Remove(videoKey);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _frames.Forget(videoKey);
        }
    }

    /// <summary>
    /// Every row of a canvas shows the same live, so they all carry the same status: the live page
    /// lists them one under the other and a row left behind at OFFLINE is a row that looks broken.
    /// </summary>
    private void MarkRows(IReadOnlyList<VideoEntity> rows, LiveStatus status, string message)
    {
        foreach (var row in rows)
        {
            row.LiveStatus = status;
            row.Message = message;
            row.LastTimeStampBeforeStop = status == LiveStatus.Stopped ? row.LastTimeStampBeforeStop : 0;
            _videoRepository.Update(row);
        }

        _notifier.Raise();
    }

    /// <summary>The configuration of the video, read again on every pass so a live change is seen.</summary>
    private VideoSettingEntity FindSetting(int videoKey)
    {
        var settingId = _videoRepository.FindByPkid(videoKey)?.VideoSettingId;
        var videoSetting = settingId is { } id ? _videoSettingRepository.FindById(id) : null;

        return videoSetting ?? throw new InvalidOperationException(
            $"video setting not found for video {videoKey} (videoSettingId={settingId?.ToString() ?? "null"})");
    }

    /// <summary>Why the monitor stopped watching a transcode.</summary>
    private enum StreamOutcome
    {
        /// <summary>The ffmpeg process ended on its own: the caller reads its exit code.</summary>
        Finished,

        /// <summary>The user stopped the live: the position has to be recorded and the video left alone.</summary>
        Stopped,

        /// <summary>The encoder configuration changed: start again from the current position.</summary>
        Reconfigured
    }

    /// <summary>
    /// Polls the stop flag like the Java version polled it every 50 frames, but also reacts
    /// immediately to the in-memory stop signal and to a change of parameters made from the
    /// preview page.
    /// </summary>
    private async Task<StreamOutcome> MonitorAsync(
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
                await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
                return StreamOutcome.Stopped;
            }

            var video = _videoRepository.FindByPkid(videoKey);
            if (video is null)
            {
                _logger.LogWarning(
                    "{Message}", _localizer.PrintMessage("error.during.retrieved.video", [inputPath, videoLiveHistoryPkid]));
                return StreamOutcome.Finished;
            }

            if (video.ShouldBeStop)
            {
                await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
                return StreamOutcome.Stopped;
            }

            if (session.RestartRequested)
            {
                return StreamOutcome.Reconfigured;
            }

            if (session.HasExited)
            {
                return StreamOutcome.Finished;
            }

            try
            {
                await Task.Delay(StopFlagPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return StreamOutcome.Finished;
            }
        }
    }

    private async Task StopAndRecordAsync(
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

    /// <summary>
    /// Rows that are streamed together: the files of a playlist, or the sources of one canvas.
    /// <see cref="ScenePkid"/> is what tells them apart.
    /// </summary>
    private sealed record VideoGroup(long? ScenePkid, List<VideoEntity> Videos);
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
