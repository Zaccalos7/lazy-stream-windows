using System.Globalization;
using Orbis.Stream.Core.Contracts;
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
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _spotsToPlay = new();

    public void EnqueueSpot(string spotPath)
    {
        _spotsToPlay.Enqueue(spotPath);
    }

    private static readonly TimeSpan StopFlagPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How close to the end a video has to be to count as streamed through.</summary>
    private const int FinishedToleranceMilliseconds = 250;

    /// <summary>
    /// What a canvas is captured at when nothing else says: gdigrab reads 5 frames a second unless
    /// it is told otherwise, and a live at 5 frames a second is not a watchable stream.
    /// </summary>
    private const double DefaultCanvasFrameRate = 30d;

    /// <summary>
    /// How long a live has to have been on air for its reconnections to be forgiven: a live that
    /// drops once an hour is a live on a bad network, not one that should end after three drops.
    /// </summary>
    private static readonly TimeSpan StableOnAir = TimeSpan.FromSeconds(60);

    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly SceneRepository _sceneRepository;
    private readonly FfmpegProbe _probe;
    private readonly FfmpegToolLocator _locator;
    private readonly StreamingSessionRegistry _sessions;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly LivePreviewFrames _frames;
    private readonly MediaProxyService _proxies;
    private readonly EncoderTuningService _tuning;
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
        MediaProxyService proxies,
        EncoderTuningService tuning,
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
        _proxies = proxies;
        _tuning = tuning;
        _logger = logger;
    }

    public async Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        RelayTransport? transport,
        CancellationToken cancellationToken)
    {
        outputUrl = StreamPlatforms.NormalizeIngestUrl(outputUrl);

        // One publish for the whole live: every video, spot and restarted pass below goes out on
        // it, so the platform never sees the live end and start again in between.
        await using var output = LiveOutput.For(outputUrl, _locator, _logger, transport);
        await StreamGroupsAsync(videos, outputUrl, output, videoLiveHistoryPkid, cancellationToken).ConfigureAwait(false);
        if (output is not null)
        {
            await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StreamGroupsAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        LiveOutput? output,
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
                await StreamSceneAsync(scene.Videos, outputUrl, output, videoLiveHistoryPkid, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var queue = (await PlanAsync(files, cancellationToken).ConfigureAwait(false)).ToList();

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
            while (_spotsToPlay.TryDequeue(out var spotPath))
            {
                var videoSetting = FindSetting(queue[i].Video.Pkid);
                var spotOutcome = await StreamSpotAsync(
                    spotPath, outputUrl, output, videoLiveHistoryPkid, queue[i].Video.Pkid, videoSetting, cancellationToken)
                    .ConfigureAwait(false);

                if (spotOutcome == StreamOutcome.Stopped)
                {
                    return;
                }
            }

            var planned = queue[i];
            cancellationToken.ThrowIfCancellationRequested();

            // A stop is for the whole playlist, not for the video that happened to be on air: the
            // next one must not go live by itself. It stays where it is, so a play resumes here.
            var isLast = i == queue.Count - 1;
            var outcome = await StreamVideoAsync(planned, outputUrl, output, videoLiveHistoryPkid, cancellationToken, markEndedOnFinish: isLast)
                .ConfigureAwait(false);
            if (outcome == StreamOutcome.Stopped)
            {
                return;
            }
            if (outcome == StreamOutcome.Yielded)
            {
                var pausedPosition = planned.Video.LastTimeStampBeforeStop;
                while (_spotsToPlay.TryDequeue(out var spotPath))
                {
                    var videoSetting = FindSetting(planned.Video.Pkid);
                    var spotOutcome = await StreamSpotAsync(
                        spotPath, outputUrl, output, videoLiveHistoryPkid, planned.Video.Pkid, videoSetting, cancellationToken)
                        .ConfigureAwait(false);

                    if (spotOutcome == StreamOutcome.Stopped)
                    {
                        var stopped = _localizer.PrintMessage("live.stopped");
                        SaveMessageOnVideoLiveHistory(stopped, videoLiveHistoryPkid, planned.Video.VideoPath, LiveStatus.Stopped, null, pausedPosition);
                        return;
                    }
                }

                // Back to the video at once: the relay is still sending the end of the spot, and that
                // is the time the video has to start in, not a pause to add on top of it.
                // Re-evaluate current video from its saved position
                var reloaded = _videoRepository.FindByPkid(planned.Video.Pkid) ?? planned.Video;
                var resumeFrom = TimeSpan.FromMilliseconds(reloaded.LastTimeStampBeforeStop > 0 ? reloaded.LastTimeStampBeforeStop : pausedPosition);
                reloaded.LastTimeStampBeforeStop = (long)resumeFrom.TotalMilliseconds;
                _videoRepository.Update(reloaded);

                queue[i] = new PlannedVideo(reloaded, planned.Probe, resumeFrom);

                i--;
                continue;
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
    private async Task<StreamOutcome> StreamVideoAsync(
        PlannedVideo planned,
        string outputUrl,
        LiveOutput? output,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken,
        bool markEndedOnFinish = true)
    {
        var video = planned.Video;
        var inputPath = video.VideoPath;
        var videoKey = video.Pkid;

        // The session of the pass in progress, kept out of the <c>using</c> of an iteration so that
        // the new one is registered before the old one is killed. A preview that asks in between the
        // two has to see the live going on, not a moment where nothing is running.
        FfmpegStreamingSession? session = null;
        var reconnects = new ReconnectBudget();

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

                var quality = await QualityOfAsync(videoSetting, FfmpegCommandBuilder.ResolveOutput(videoSetting, probe), cancellationToken)
                    .ConfigureAwait(false);
                var next = FfmpegStreamingSession.Start(
                    _locator, videoKey, inputPath, outputUrl, videoSetting, probe, _logger, resumeFrom, _frames.PathOf(videoKey),
                    output: output, quality: quality);

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                // A started process is not a live: the row says it is connecting until the ingest
                // takes the stream, and only then that it is streaming.
                var platform = PlatformName(next);
                var connecting = _localizer.PrintMessage("video.live.connecting", [inputPath, platform]);
                _logger.LogInformation("{Message}", connecting);
                SaveMessageOnVideoLiveHistory(
                    connecting, videoLiveHistoryPkid, inputPath, LiveStatus.Live, DateTime.Now);

                var outcome = await MonitorAsync(
                        session,
                        videoKey,
                        videoLiveHistoryPkid,
                        inputPath,
                        () =>
                        {
                            var startedMessage = _localizer.PrintMessage("video.live.started", [inputPath]);
                            _logger.LogInformation("{Message}", startedMessage);
                            SaveMessageOnVideoLiveHistory(
                                startedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Live, null);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    return StreamOutcome.Stopped;
                }

                if (outcome == StreamOutcome.NeverOnAir)
                {
                    var notReceived = await NotReceivedMessageAsync(session, inputPath, platform).ConfigureAwait(false);
                    _logger.LogError("{Message}", notReceived);
                    SaveMessageOnVideoLiveHistory(
                        notReceived, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now, (long)resumeFrom.TotalMilliseconds);
                    return StreamOutcome.Finished;
                }

                if (outcome == StreamOutcome.Reconfigured)
                {
                    // Where the transcode got to is where the new one carries on from: the live
                    // skips nothing, it only pays for the second ffmpeg starting. On a shared
                    // connection the old encoder goes first, so the point it got to stops moving.
                    if (session.SharesOutput)
                    {
                        await session.HandOverAsync().ConfigureAwait(false);
                    }

                    resumeFrom = TimeSpan.FromMilliseconds(session.ContinuationMilliseconds);
                    var reconfigured = _localizer.PrintMessage("video.live.reconfigured", [inputPath]);
                    _logger.LogInformation("{Message}", reconfigured);
                    SaveMessageOnVideoLiveHistory(
                        reconfigured, videoLiveHistoryPkid, inputPath, LiveStatus.Live, null);
                    continue;
                }

                if (outcome == StreamOutcome.Yielded)
                {
                    // The video makes way for the spot without closing the connection: what the
                    // relay already holds of it still goes on air, and the spot follows it.
                    await session.HandOverAsync().ConfigureAwait(false);
                    var pausedPosition = session.ContinuationMilliseconds;
                    var videoEntity = _videoRepository.FindByPkid(videoKey);
                    if (videoEntity is not null)
                    {
                        videoEntity.LastTimeStampBeforeStop = Math.Max(0, pausedPosition);
                        videoEntity.LiveStatus = LiveStatus.Live;
                        var spotYieldMsg = _localizer.PrintMessage("live.yielded") ?? "Spot in corso...";
                        videoEntity.Message = spotYieldMsg;
                        _videoRepository.Update(videoEntity);
                        _notifier.Raise();
                    }

                    _sessions.Remove(videoKey);
                    await session.DisposeAsync().ConfigureAwait(false);
                    _frames.Forget(videoKey);
                    session = null;
                    return StreamOutcome.Yielded;
                }

                var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);

                // Check stop condition first (needs session for StopAndRecordAsync)
                var wasStopped = session.StopRequested || (exitCode != 0 && _videoRepository.FindByPkid(videoKey)?.ShouldBeStop == true);

                if (wasStopped)
                {
                    await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath).ConfigureAwait(false);
                    return StreamOutcome.Stopped;
                }

                // A live that was on air and broke - the network dropped, the ingest closed the
                // connection, the stream stopped moving - carries on from where it got to.
                // An end reached by position kills the processes too: that exit code is not a break.
                if (!session.EndedNaturally
                    && (outcome == StreamOutcome.Stalled || exitCode != 0)
                    && await ShouldReconnectAsync(
                            session,
                            reconnects,
                            inputPath,
                            message => SaveMessageOnVideoLiveHistory(message, videoLiveHistoryPkid, inputPath, LiveStatus.Live, null),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    resumeFrom = TimeSpan.FromMilliseconds(session.PositionMilliseconds);
                    continue;
                }

                if (outcome == StreamOutcome.Stalled)
                {
                    errorOutput = _localizer.PrintMessage("video.live.stalled", [inputPath, PlatformName(session)])
                        + "\n" + errorOutput;
                }

                // Video ended normally (exitCode == 0): save position and clean up immediately
                var finalPosition = session.ContinuationMilliseconds;

                // Clean up session immediately so preview sees live as ended
                _sessions.Remove(videoKey);
                await session.DisposeAsync().ConfigureAwait(false);
                _frames.Forget(videoKey);
                var endedNaturally = session.EndedNaturally;
                session = null;

                var durationMs = probe.DurationSeconds > 0 ? (long)(probe.DurationSeconds * 1000) : 0;
                var reachedEnd = endedNaturally || (durationMs > 0 && finalPosition >= durationMs - FinishedToleranceMilliseconds);

                if (exitCode == 0 || reachedEnd)
                {
                    if (reachedEnd)
                    {
                        if (markEndedOnFinish)
                        {
                            var endedMessage = _localizer.PrintMessage("video.live.ended");
                            _logger.LogInformation("{Message}", endedMessage);
                            SaveMessageOnVideoLiveHistory(
                                endedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Ended, null, finalPosition);
                        }
                        else
                        {
                            // Don't mark as ended if this is an intermediate video in a playlist
                            // The next video will take over
                            _logger.LogInformation("Video ended, continuing to next in playlist");
                            SaveMessageOnVideoLiveHistory(
                                string.Empty, videoLiveHistoryPkid, inputPath, LiveStatus.Offline, null, finalPosition);
                        }
                        return StreamOutcome.Finished;
                    }
                    else
                    {
                        resumeFrom = TimeSpan.FromMilliseconds(finalPosition);
                        var videoEntity = _videoRepository.FindByPkid(videoKey);
                        if (videoEntity is not null)
                        {
                            videoEntity.LastTimeStampBeforeStop = finalPosition;
                            _videoRepository.Update(videoEntity);
                        }
                        continue;
                    }
                }

                var streamingError = _localizer.PrintMessage(
                    "error.during.streaming.video", [inputPath, videoLiveHistoryPkid])
                    + "\n"
                    + (string.IsNullOrWhiteSpace(errorOutput) ? $"ffmpeg exited with code {exitCode}" : errorOutput.Trim());
                _logger.LogError("{Message}", streamingError);
                SaveMessageOnVideoLiveHistory(
                    streamingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now, 0);
                return StreamOutcome.Finished;
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
            return StreamOutcome.Finished;
        }
        finally
        {
            // Session already cleaned up in the normal flow, but ensure it's gone
            _sessions.Remove(videoKey);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _frames.Forget(videoKey);
        }
    }

    /// <summary>
    /// What ffprobe says about every file of the canvas, asked once: the sound of a file decides
    /// whether it joins the mix, and its length decides whether the live waits for it.
    /// </summary>
    /// <summary>
    /// What ffmpeg opens for each file of a canvas: its light copy when it has one that fits the
    /// tile, otherwise the file, on the GPU when the GPU decodes it faster. Decided once a live, so
    /// a reconfigured pass resumes in the same picture it left; the copy is on the same timeline,
    /// so the position is the same either way.
    /// </summary>
    private Dictionary<VideoEntity, MediaInput> FileInputsOf(
        IReadOnlyList<VideoEntity> rows, IReadOnlyDictionary<string, MediaProbeResult> probes)
    {
        var inputs = new Dictionary<VideoEntity, MediaInput>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows.Where(row => row.SourceKind == SourceKind.File))
        {
            inputs[row] = probes.TryGetValue(row.VideoPath, out var probe)
                ? _proxies.Resolve(row.VideoPath, probe, row.Width ?? 0, row.Height ?? 0)
                : new MediaInput(row.VideoPath, false);
        }

        return inputs;
    }

    private async Task<Dictionary<string, MediaProbeResult>> ProbedFilesAsync(
        IReadOnlyList<VideoEntity> rows,
        CancellationToken cancellationToken)
    {
        var probes = new Dictionary<string, MediaProbeResult>(StringComparer.Ordinal);
        foreach (var row in rows.Where(row => row.SourceKind == SourceKind.File))
        {
            if (probes.ContainsKey(row.VideoPath))
            {
                continue;
            }

            try
            {
                probes[row.VideoPath] = await _probe.ProbeAsync(row.VideoPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not probe {Path}", row.VideoPath);
            }
        }

        return probes;
    }

    /// <summary>
    /// The files whose sound was asked for but that have none. A video without an audio track is
    /// common, and <c>[n:a]</c> on it fails the whole graph before the first frame: such a file
    /// stays on the canvas and simply leaves the mix. A file ffprobe cannot read counts as silent,
    /// so the error the user sees is ffmpeg's about the file, not a filter one about its sound.
    /// </summary>
    private static HashSet<VideoEntity> SilentFilesOf(
        IReadOnlyList<VideoEntity> rows,
        IReadOnlyDictionary<string, MediaProbeResult> probes)
    {
        var silent = new HashSet<VideoEntity>();
        foreach (var row in rows.Where(row => row.SourceKind == SourceKind.File && row.AudioEnabled))
        {
            if (!probes.TryGetValue(row.VideoPath, out var probe) || !probe.HasAudio)
            {
                silent.Add(row);
            }
        }

        return silent;
    }

    /// <summary>
    /// How long a canvas of files streams: the length of its longest file.
    /// <para>A canvas is not a playlist, but the end of it is decided the same way: a video that
    /// runs out while the others are still going leaves its shape on the canvas (the overlay holds
    /// its last frame) and the live goes on, and when the longest file is over the live is over.
    /// A canvas with a capture device on it has no end: a device produces frames for ever, so
    /// null is the answer and the live lasts until it is stopped.</para>
    /// <para>A file ffprobe could not read has no length to be measured with, so it is not what
    /// the live is measured against: a live that never ends because of a file nothing can read is
    /// better than one that ends on the first guess.</para>
    /// </summary>
    internal static TimeSpan? LongestFileDuration(
        IReadOnlyList<VideoEntity> rows,
        IReadOnlyDictionary<string, MediaProbeResult> probes)
    {
        var seconds = rows
            .Where(row => row.SourceKind == SourceKind.File)
            .Select(row => probes.TryGetValue(row.VideoPath, out var probe) ? probe.DurationSeconds : 0d)
            .Where(value => value > 0)
            .DefaultIfEmpty(0d)
            .Max();

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
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
    /// opened together and laid over each other, so the pass lasts until the user stops it, or
    /// until the longest of its files is over when the canvas is made of nothing else. That is the
    /// same end a playlist has, measured the same way, and the live stops as cleanly as the button.
    /// </summary>
    private async Task StreamSceneAsync(
        IReadOnlyList<VideoEntity> rows,
        string outputUrl,
        LiveOutput? output,
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

        // A canvas made only of files has an end, and it is the end of the longest of them: a
        // video that runs out first leaves its shape on the canvas and the live goes on, the way a
        // playlist goes on to the next video.
        var onlyFiles = rows.All(row => row.SourceKind == SourceKind.File);
        var videoKey = baseRow.Pkid;
        var description = _localizer.PrintMessage("video.live.scene", [rows.Count.ToString(CultureInfo.InvariantCulture)]);
        FfmpegStreamingSession? session = null;
        var reconnects = new ReconnectBudget();

        try
        {
            var initialPosition = baseRow.LastTimeStampBeforeStop;
            var resumeFrom = initialPosition > 0 ? TimeSpan.FromMilliseconds(initialPosition) : TimeSpan.Zero;
            var probes = await ProbedFilesAsync(rows, cancellationToken).ConfigureAwait(false);
            var silent = SilentFilesOf(rows, probes);
            var duration = onlyFiles ? LongestFileDuration(rows, probes) : null;
            var inputs = FileInputsOf(rows, probes);
            if (duration is { } canvasLength)
            {
                _logger.LogInformation(
                    "The canvas streams for {Seconds:0.##} seconds, the length of its longest file",
                    canvasLength.TotalSeconds);
            }

            if (resumeFrom > TimeSpan.Zero)
            {
                var resumed = _localizer.PrintMessage("video.live.resumed", [description, Seconds(resumeFrom)]);
                _logger.LogInformation("{Message}", resumed);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var videoSetting = CanvasSetting(videoKey, rows);

                // The volume of a source is changed from the preview while the live runs, and the
                // change arrives as a new pass: each pass reads it again.
                foreach (var row in rows)
                {
                    row.Volume = _videoRepository.FindByPkid(row.Pkid)?.Volume ?? row.Volume;
                }

                var items = rows
                    .Select(row => new FfmpegCompositionItem(
                        row.SourceKind,
                        row.SourceKind == SourceKind.File ? inputs[row].Path : row.SourceTarget ?? string.Empty,
                        row.X ?? 0,
                        row.Y ?? 0,
                        row.Width ?? 0,
                        row.Height ?? 0,
                        row.AudioEnabled && !silent.Contains(row),
                        row.SourceKind == SourceKind.File && inputs[row].HardwareDecoding,
                        row.Volume))
                    .ToList();

                var canvasRate = videoSetting.FrameRate is > 0 ? videoSetting.FrameRate.Value : DefaultCanvasFrameRate;
                var quality = await QualityOfAsync(videoSetting, new MediaOutput(canvasWidth, canvasHeight, canvasRate), cancellationToken)
                    .ConfigureAwait(false);
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
                    duration,
                    _frames.PathOf(videoKey),
                    output: output,
                    quality: quality);

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                var platform = PlatformName(next);
                var connecting = _localizer.PrintMessage("video.live.connecting", [description, platform]);
                MarkRows(rows, LiveStatus.Live, connecting);
                _logger.LogInformation("{Message}", connecting);

                var outcome = await MonitorAsync(
                        session,
                        videoKey,
                        videoLiveHistoryPkid,
                        baseRow.VideoPath,
                        () =>
                        {
                            MarkRows(rows, LiveStatus.Live, description);
                            _logger.LogInformation("{Message}", description);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    var pausedPosition = session?.PositionMilliseconds ?? 0;
                    if (pausedPosition <= 0 && resumeFrom > TimeSpan.Zero)
                    {
                        pausedPosition = (long)resumeFrom.TotalMilliseconds;
                    }
                    foreach (var row in rows)
                    {
                        row.LastTimeStampBeforeStop = Math.Max(0, pausedPosition);
                    }
                    var stopped = session?.StopReason ?? _localizer.PrintMessage("live.stopped");
                    MarkRows(rows, LiveStatus.Stopped, stopped);
                    return;
                }

                if (outcome == StreamOutcome.NeverOnAir)
                {
                    var notReceived = await NotReceivedMessageAsync(session, description, platform).ConfigureAwait(false);
                    _logger.LogError("{Message}", notReceived);
                    MarkRows(rows, LiveStatus.Error, notReceived);
                    return;
                }

                if (outcome == StreamOutcome.Reconfigured)
                {
                    if (session.SharesOutput)
                    {
                        await session.HandOverAsync().ConfigureAwait(false);
                    }

                    resumeFrom = TimeSpan.FromMilliseconds(session.ContinuationMilliseconds);
                    var reconfigured = _localizer.PrintMessage("video.live.reconfigured", [description]);
                    _logger.LogInformation("{Message}", reconfigured);
                    SaveMessageOnVideoLiveHistory(
                        reconfigured, videoLiveHistoryPkid, baseRow.VideoPath, LiveStatus.Live, null);
                    continue;
                }

                if (outcome == StreamOutcome.Yielded)
                {
                    await session.HandOverAsync().ConfigureAwait(false);
                    var pausedPosition = session.ContinuationMilliseconds;
                    if (pausedPosition <= 0 && resumeFrom > TimeSpan.Zero)
                    {
                        pausedPosition = (long)resumeFrom.TotalMilliseconds;
                    }
                    resumeFrom = TimeSpan.FromMilliseconds(pausedPosition);
                    foreach (var row in rows)
                    {
                        row.LastTimeStampBeforeStop = Math.Max(0, pausedPosition);
                        row.LiveStatus = LiveStatus.Live;
                        var spotYieldMsg = _localizer.PrintMessage("live.yielded") ?? "Spot in corso...";
                        row.Message = spotYieldMsg;
                        _videoRepository.Update(row);
                    }
                    _notifier.Raise();

                    _sessions.Remove(videoKey);
                    await session.DisposeAsync().ConfigureAwait(false);
                    _frames.Forget(videoKey);
                    session = null;

                    while (_spotsToPlay.TryDequeue(out var spotPath))
                    {
                        var spotOutcome = await StreamSpotAsync(
                            spotPath, outputUrl, output, videoLiveHistoryPkid, videoKey, videoSetting, cancellationToken)
                            .ConfigureAwait(false);

                        if (spotOutcome == StreamOutcome.Stopped)
                        {
                            var stopped = _localizer.PrintMessage("live.stopped");
                            MarkRows(rows, LiveStatus.Stopped, stopped);
                            return;
                        }
                    }

                    var currentBase = _videoRepository.FindByPkid(videoKey) ?? baseRow;
                    if (currentBase.LastTimeStampBeforeStop > 0)
                    {
                        resumeFrom = TimeSpan.FromMilliseconds(currentBase.LastTimeStampBeforeStop);
                    }
                    else if (pausedPosition > 0)
                    {
                        resumeFrom = TimeSpan.FromMilliseconds(pausedPosition);
                        foreach (var row in rows)
                        {
                            row.LastTimeStampBeforeStop = pausedPosition;
                            _videoRepository.Update(row);
                        }
                    }

                    continue;
                }

                var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);

                // Check stop condition first (needs session for StopAsync)
                var wasStopped = session.StopRequested || (exitCode != 0 && _videoRepository.FindByPkid(videoKey)?.ShouldBeStop == true);

                if (wasStopped)
                {
                    var stopped = session.StopReason ?? _localizer.PrintMessage("live.stopped");
                    await session.StopAsync().ConfigureAwait(false);
                    MarkRows(rows, LiveStatus.Stopped, stopped);
                    return;
                }

                // An end reached by position kills the processes too: that exit code is not a break.
                if (!session.EndedNaturally
                    && (outcome == StreamOutcome.Stalled || exitCode != 0)
                    && await ShouldReconnectAsync(
                            session, reconnects, description, message => MarkRows(rows, LiveStatus.Live, message), cancellationToken)
                        .ConfigureAwait(false))
                {
                    resumeFrom = TimeSpan.FromMilliseconds(session.PositionMilliseconds);
                    continue;
                }

                if (outcome == StreamOutcome.Stalled)
                {
                    errorOutput = _localizer.PrintMessage("video.live.stalled", [description, PlatformName(session)])
                        + "\n" + errorOutput;
                }

                // Video ended normally (exitCode == 0): save position and clean up immediately
                var finalPosition = session.ContinuationMilliseconds;

                // Clean up session immediately so preview sees live as ended
                _sessions.Remove(videoKey);
                await session.DisposeAsync().ConfigureAwait(false);
                _frames.Forget(videoKey);
                var endedNaturally = session.EndedNaturally;
                session = null;

                var durationMs = duration is { } d ? (long)(d.TotalSeconds * 1000) : 0;
                var reachedEnd = endedNaturally || (durationMs > 0 && finalPosition >= durationMs - FinishedToleranceMilliseconds);

                // If the scene has only file sources and ffmpeg exited cleanly, the video ended.
                // End the live instead of restarting the loop.
                if (onlyFiles && (exitCode == 0 || reachedEnd))
                {
                    if (reachedEnd)
                    {
                        var endedMessage = _localizer.PrintMessage("video.live.ended");
                        _logger.LogInformation("{Message}", endedMessage);
                        foreach (var row in rows)
                        {
                            row.LastTimeStampBeforeStop = finalPosition;
                        }
                        MarkRows(rows, LiveStatus.Ended, endedMessage);
                        return;
                    }
                    else
                    {
                        resumeFrom = TimeSpan.FromMilliseconds(finalPosition);
                        foreach (var row in rows)
                        {
                            row.LastTimeStampBeforeStop = finalPosition;
                            _videoRepository.Update(row);
                        }
                        continue;
                    }
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
            // Session already cleaned up in the normal flow, but ensure it's gone
            _sessions.Remove(videoKey);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _frames.Forget(videoKey);
        }
    }

    /// <summary>
    /// Streams an ad/intermission spot to the current live output URL without ending the live stream.
    /// The spot is registered under the driving row's key so the preview page continues to display frames,
    /// and the database rows remain in Live status throughout.
    /// </summary>
    private async Task<StreamOutcome> StreamSpotAsync(
        string spotPath,
        string outputUrl,
        LiveOutput? output,
        long videoLiveHistoryPkid,
        int drivingVideoKey,
        VideoSettingEntity videoSetting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var probe = await _probe.ProbeAsync(spotPath, cancellationToken).ConfigureAwait(false);
        var spotName = Path.GetFileName(spotPath);
        var spotMessage = _localizer.PrintMessage("video.live.spot.playing", [spotName]) ?? $"Spot: {spotName}";
        _logger.LogInformation("Streaming spot {SpotPath} to {OutputUrl}", spotPath, outputUrl);

        var drivingVideo = _videoRepository.FindByPkid(drivingVideoKey);
        if (drivingVideo is not null)
        {
            drivingVideo.LiveStatus = LiveStatus.Live;
            drivingVideo.Message = spotMessage;
            _videoRepository.Update(drivingVideo);
            _notifier.Raise();
        }

        FfmpegStreamingSession? session = null;
        try
        {
            var quality = await QualityOfAsync(videoSetting, FfmpegCommandBuilder.ResolveOutput(videoSetting, probe), cancellationToken)
                .ConfigureAwait(false);
            session = FfmpegStreamingSession.Start(
                _locator,
                drivingVideoKey,
                spotPath,
                outputUrl,
                videoSetting,
                probe,
                _logger,
                TimeSpan.Zero,
                _frames.PathOf(drivingVideoKey),
                output: output,
                quality: quality);

            _sessions.Register(session);

            var outcome = await MonitorAsync(
                session,
                drivingVideoKey,
                videoLiveHistoryPkid,
                spotPath,
                () => { },
                cancellationToken).ConfigureAwait(false);

            if (outcome == StreamOutcome.Stopped)
            {
                return StreamOutcome.Stopped;
            }

            var exitCode = await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return exitCode == 0 || session.EndedNaturally ? StreamOutcome.Finished : StreamOutcome.Finished;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed streaming spot {SpotPath}", spotPath);
            return StreamOutcome.Finished;
        }
        finally
        {
            _sessions.Remove(drivingVideoKey);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _frames.Forget(drivingVideoKey);
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
            _videoRepository.Update(row);
        }

        _notifier.Raise();
    }

    /// <summary>
    /// The encoder level of a pass: the one of the setting, or the one this machine keeps up with
    /// at this size when the setting leaves it to the machine. Measured once, then known.
    /// </summary>
    private Task<EncoderQuality> QualityOfAsync(VideoSettingEntity setting, MediaOutput output, CancellationToken cancellationToken) =>
        _tuning.ResolveAsync(setting, output.Width, output.Height, output.FrameRate, cancellationToken);

    /// <summary>The configuration of the video, read again on every pass so a live change is seen.</summary>
    private VideoSettingEntity FindSetting(int videoKey)
    {
        var settingId = _videoRepository.FindByPkid(videoKey)?.VideoSettingId;
        var videoSetting = settingId is { } id ? _videoSettingRepository.FindById(id) : null;

        return videoSetting ?? throw new InvalidOperationException(
            $"video setting not found for video {videoKey} (videoSettingId={settingId?.ToString() ?? "null"})");
    }

    /// <summary>
    /// The setting a canvas goes on air with, with the low latency switch decided for it.
    /// <para>A composition decodes every source on it, scales it and lays it over the others, and
    /// only then encodes one picture. That is the one case where the machine, and not the network,
    /// is what the live is waiting on, and a canvas that runs out of CPU does not fall behind
    /// cleanly: it drops frames inside the picture, which is what a viewer reads as the live
    /// stuttering. The low latency defaults are the ones that spend the least CPU per frame, so a
    /// canvas of two or more pictures turns them on for itself.</para>
    /// <para>The setting the rows were given is never edited here. It may be a seeded default, one
    /// the wizard offers, or one another live is streaming with right now, and changing it would
    /// change all of them under the user's feet. Rows that already own their setting keep that one
    /// with the switch turned on; rows sharing somebody else's get a copy of their own, and that
    /// copy is what they go on air with. The configuration the user chose stays as it was, and the
    /// same goes for a canvas of a single source, which is not a composition and streams its
    /// setting exactly as the user left it.</para>
    /// </summary>
    private VideoSettingEntity CanvasSetting(int videoKey, IReadOnlyList<VideoEntity> rows)
    {
        var setting = FindSetting(videoKey);
        if (VideoSettingLatency.IsOn(setting) || rows.Count(row => row.SourceKind.HasPicture()) < 2)
        {
            return setting;
        }

        // Rows that own their setting are edited where it is; rows sharing one are given a copy,
        // which is what LinkSetting does when the live page saves a setting by hand.
        var shared = setting.Id is not { } id || !IsOnlyOf(id, rows);
        var target = shared
            ? VideoSettingsRequest.FromEntity(setting).ToEntity()
            : setting;

        target.Id = shared ? null : setting.Id;
        target.IsDefaultConfiguration = shared ? false : setting.IsDefaultConfiguration;
        target.IsVideoAndAudioSettingActive = shared ? false : setting.IsVideoAndAudioSettingActive;
        target.LastModified = DateTime.Now;

        VideoSettingLatency.Set(target, true);
        target.Id ??= _videoSettingRepository.Insert(target);
        _videoSettingRepository.Update(target);

        foreach (var row in rows.Where(row => row.VideoSettingId != target.Id))
        {
            _videoRepository.SetVideoSetting(row.Pkid, target.Id.Value);
        }

        _logger.LogInformation(
            "The canvas of {Rows} sources goes on air with low latency on, on the setting '{Setting}' of its own",
            rows.Count,
            target.Title);

        return _videoSettingRepository.FindById(target.Id.Value) ?? target;
    }

    /// <summary>
    /// Whether these rows are the only ones a setting belongs to: a seeded default is offered by
    /// the wizard to every live, so it is never theirs to edit, and a setting another live is
    /// streaming with would change under the feet of that live. A setting the rows carry alone is
    /// theirs, and so is one they got from the wizard and nobody else is using.
    /// </summary>
    private bool IsOnlyOf(int settingId, IReadOnlyList<VideoEntity> rows) =>
        _videoSettingRepository.FindById(settingId) is { IsDefaultConfiguration: not true }
        && _videoRepository.FindPkidsByVideoSettingId(settingId).All(pkid => rows.Any(row => row.Pkid == pkid));

    /// <summary>Why the monitor stopped watching a transcode.</summary>
    private enum StreamOutcome
    {
        Yielded,
        /// <summary>The ffmpeg process ended on its own: the caller reads its exit code.</summary>
        Finished,

        /// <summary>The user stopped the live: the position has to be recorded and the video left alone.</summary>
        Stopped,

        /// <summary>The encoder configuration changed: start again from the current position.</summary>
        Reconfigured,

        /// <summary>
        /// The ingest never took the stream within the connect timeout of the platform. The live
        /// was killed; it is an error, not something to retry, since a wrong key or a blocked
        /// network fails the same way the second time.
        /// </summary>
        NeverOnAir,

        /// <summary>The live was on air and stopped moving for longer than the platform allows: killed.</summary>
        Stalled
    }

    /// <summary>
    /// Polls the stop flag like the Java version polled it every 50 frames, but also reacts
    /// immediately to the in-memory stop signal and to a change of parameters made from the
    /// preview page. It is also what tells a live from a process: <paramref name="onAir"/> runs
    /// once, when the ingest starts taking the stream, and a live that never gets there, or that
    /// stops moving once it did, is killed instead of being left on the page as LIVE.
    /// </summary>
    private async Task<StreamOutcome> MonitorAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath,
        Action onAir,
        CancellationToken cancellationToken)
    {
        var announced = false;
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

            if (session.YieldRequested)
            {
                return StreamOutcome.Yielded;
            }

            var durationMs = session.Probe.DurationSeconds > 0 ? (long)(session.Probe.DurationSeconds * 1000) : 0;
            if (durationMs > 0 && session.ContinuationMilliseconds >= durationMs)
            {
                await session.EndNaturallyAsync().ConfigureAwait(false);
                return StreamOutcome.Finished;
            }

            if (session.HasExited)
            {
                return StreamOutcome.Finished;
            }

            var profile = session.Profile;
            if (!session.IsOnAir)
            {
                if (DateTimeOffset.UtcNow - session.StartedAt > profile.ConnectTimeout)
                {
                    _logger.LogWarning(
                        "No byte reached {Platform} in {Seconds}s: the live is not on air",
                        profile.Platform,
                        profile.ConnectTimeout.TotalSeconds);
                    await session.AbortAsync().ConfigureAwait(false);
                    return StreamOutcome.NeverOnAir;
                }
            }
            else
            {
                if (!announced)
                {
                    announced = true;
                    onAir();
                }

                if (session.SinceLastAdvance > profile.StallTimeout)
                {
                    _logger.LogWarning(
                        "The live to {Platform} has not moved for {Seconds:0}s: it is stalled",
                        profile.Platform,
                        session.SinceLastAdvance.TotalSeconds);
                    await session.AbortAsync().ConfigureAwait(false);
                    return StreamOutcome.Stalled;
                }
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

    /// <summary>
    /// Whether a live that broke is reconnected, and the wait before it is. Only a live that was
    /// on air is: one that never got there failed for a reason a second try does not change. A
    /// live that stayed on air for a while starts its budget over, and the wait grows with every
    /// attempt in a row so a network that is down is not hammered.
    /// </summary>
    private async Task<bool> ShouldReconnectAsync(
        FfmpegStreamingSession session,
        ReconnectBudget budget,
        string label,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        var profile = session.Profile;
        if (!session.IsOnAir || profile.ReconnectAttempts <= 0)
        {
            return false;
        }

        if (session.OnAirFor >= StableOnAir)
        {
            budget.Used = 0;
        }

        if (budget.Used >= profile.ReconnectAttempts)
        {
            return false;
        }

        budget.Used++;
        var message = _localizer.PrintMessage(
            "video.live.reconnecting",
            [label, PlatformName(session), budget.Used.ToString(CultureInfo.InvariantCulture), profile.ReconnectAttempts.ToString(CultureInfo.InvariantCulture)]);
        _logger.LogWarning("{Message}", message);
        report(message);

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * budget.Used)), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Why the platform never received the live, with what ffmpeg said about it.</summary>
    private async Task<string> NotReceivedMessageAsync(FfmpegStreamingSession session, string label, string platform)
    {
        var errorOutput = await session.ReadErrorAsync().ConfigureAwait(false);
        var message = _localizer.PrintMessage("video.live.not.received", [label, platform]);
        return string.IsNullOrWhiteSpace(errorOutput) ? message : message + "\n" + errorOutput.Trim();
    }

    private static string PlatformName(FfmpegStreamingSession session) => session.Profile.Platform switch
    {
        StreamPlatform.Twitch => "Twitch",
        StreamPlatform.YouTube => "YouTube",
        _ => "RTMP"
    };

    private async Task StopAndRecordAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath,
        bool isYield = false)
    {
        await session.StopAsync().ConfigureAwait(false);

        var message = isYield
            ? _localizer.PrintMessage("live.yielded") ?? "Yielded"
            : session.StopReason ?? _localizer.PrintMessage("live.stopped");
        _logger.LogInformation("{Message}", message);
        if (videoKey > 0)
        {
            _videoRepository.SetStopFlag(videoKey, false);
            SaveMessageOnVideoLiveHistory(
                message, videoLiveHistoryPkid, inputPath, isYield ? LiveStatus.Offline : LiveStatus.Stopped, null, session.PositionMilliseconds);
        }
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

    /// <summary>How many reconnections in a row a live has used.</summary>
    private sealed class ReconnectBudget
    {
        public int Used { get; set; }
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
    void EnqueueSpot(string spotPath);

    /// <param name="transport">What publishes to the ingest, when the channel asks for it; null is the platform default.</param>
    Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        RelayTransport? transport,
        CancellationToken cancellationToken);
}
