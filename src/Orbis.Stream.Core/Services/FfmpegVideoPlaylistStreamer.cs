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
    private static readonly TimeSpan StopFlagPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long an encoder may go without a lead on what is on air before the live is carried on
    /// one level lighter (see FfmpegStreamingSession.BehindFor). Long enough for a busy moment of
    /// the machine to pass on its own, short enough that the viewers do not sit through it.
    /// </summary>
    private static readonly TimeSpan AdaptAfter = TimeSpan.FromSeconds(10);

    /// <summary>What the timestamps of the last frame of a canvas round to, on top of a frame.</summary>
    private const long LastFrameSlackMilliseconds = 20;

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
    private readonly LiveTakeovers _takeovers;
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
        LiveTakeovers takeovers,
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
        _takeovers = takeovers;
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

        // From here on the live takes what the scene deck and the spot dialog ask for, and drops
        // it when it ends: a request left behind would otherwise go on air at the next play.
        var lease = _takeovers.Open(videoLiveHistoryPkid);
        try
        {
            // One publish for the whole live: every video, spot and restarted pass below goes out
            // on it, so the platform never sees the live end and start again in between.
            await using var output = LiveOutput.For(outputUrl, _locator, _logger, transport);
            await StreamGroupsAsync(videos, outputUrl, output, videoLiveHistoryPkid, cancellationToken).ConfigureAwait(false);
            if (output is not null)
            {
                await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _takeovers.Close(videoLiveHistoryPkid, lease);
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
            var planned = queue[i];

            // What was asked for while no video was on air - in between two of them - goes on
            // before the next one, rather than the moment it has started.
            var asked = new PausedProgram(
                videoLiveHistoryPkid,
                planned.Video.Pkid,
                planned.Video.VideoPath,
                (long)planned.ResumeFrom.TotalMilliseconds,
                () => FindSetting(planned.Video.Pkid),
                outputUrl,
                output,
                output?.Frame);
            if (await RunTakeoversAsync(asked, cancellationToken).ConfigureAwait(false) == StreamOutcome.Stopped)
            {
                return;
            }

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

                var quality = await QualityOfAsync(videoSetting, PictureOf(videoSetting, probe, output), output, cancellationToken)
                    .ConfigureAwait(false);
                var next = FfmpegStreamingSession.Start(
                    _locator, videoKey, inputPath, outputUrl, videoSetting, probe, _logger, resumeFrom, _frames.PathOf(videoKey),
                    output: output, quality: quality);
                next.AdaptiveQuality = AdaptiveOf(videoSetting, quality);

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
                        cancellationToken,
                        yieldWhen: () => _takeovers.HasPending(videoLiveHistoryPkid))
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
                    // The video makes way for a spot or a scene without closing the connection:
                    // what the relay already holds of it still goes on air, what was asked for
                    // follows it, and the video comes back from the point it handed over at.
                    await session.HandOverAsync().ConfigureAwait(false);
                    var picture = session.Output;
                    var pausedPosition = Math.Max(0, session.ContinuationMilliseconds);
                    if (_videoRepository.FindByPkid(videoKey) is { } paused)
                    {
                        paused.LastTimeStampBeforeStop = pausedPosition;
                        paused.LiveStatus = LiveStatus.Live;
                        _videoRepository.Update(paused);
                    }

                    _sessions.Remove(videoKey);
                    await session.DisposeAsync().ConfigureAwait(false);
                    _frames.Forget(videoKey);
                    session = null;

                    var program = new PausedProgram(
                        videoLiveHistoryPkid, videoKey, inputPath, pausedPosition, () => FindSetting(videoKey), outputUrl, output, picture);
                    if (await RunTakeoversAsync(program, cancellationToken).ConfigureAwait(false) == StreamOutcome.Stopped)
                    {
                        return StreamOutcome.Stopped;
                    }

                    resumeFrom = TimeSpan.FromMilliseconds(pausedPosition);
                    continue;
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

    /// <summary>
    /// How each overlay of the canvas is opened, asked of ffprobe once a live (see OverlayMedia):
    /// a still once, an animation in a loop, a WebM with alpha through libvpx. An overlay whose file
    /// is gone is left off the canvas: it dresses the live, and the live goes on air without it
    /// rather than not at all. One ffprobe cannot read is opened as a still, so that the error the
    /// live ends on, if it does, is ffmpeg's own about that file.
    /// </summary>
    private async Task<Dictionary<VideoEntity, OverlayMedia>> OverlaysOfAsync(
        IReadOnlyList<VideoEntity> rows,
        CancellationToken cancellationToken)
    {
        var overlays = new Dictionary<VideoEntity, OverlayMedia>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows.Where(row => row.SourceKind.IsOverlay()))
        {
            if (!File.Exists(row.VideoPath))
            {
                _logger.LogWarning("The overlay {Path} is gone: the canvas goes on air without it", row.VideoPath);
                continue;
            }

            try
            {
                overlays[row] = OverlayMedia.Of(await _probe.ProbeAsync(row.VideoPath, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not probe the overlay {Path}", row.VideoPath);
                overlays[row] = OverlayMedia.Still;
            }
        }

        return overlays;
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
        var baseRow = SceneRows.BaseOf(rows);
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
        // playlist goes on to the next video. Its overlays do not change that: they are laid over
        // the files for as long as the files last.
        var onlyFiles = rows.All(row => !row.SourceKind.IsCaptureDevice());
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
            var overlays = await OverlaysOfAsync(rows, cancellationToken).ConfigureAwait(false);
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
                    .Where(row => !row.SourceKind.IsOverlay() || overlays.ContainsKey(row))
                    .Select(row => new FfmpegCompositionItem(
                        row.SourceKind,
                        row.SourceKind switch
                        {
                            SourceKind.File => inputs[row].Path,
                            SourceKind.Overlay => row.VideoPath,
                            _ => row.SourceTarget ?? string.Empty
                        },
                        row.X ?? 0,
                        row.Y ?? 0,
                        row.Width ?? 0,
                        row.Height ?? 0,
                        row.AudioEnabled && !silent.Contains(row),
                        row.SourceKind == SourceKind.File && inputs[row].HardwareDecoding,
                        row.Volume,
                        overlays.GetValueOrDefault(row)))
                    .ToList();

                var canvasRate = videoSetting.FrameRate is > 0 ? videoSetting.FrameRate.Value : DefaultCanvasFrameRate;
                var quality = await QualityOfAsync(videoSetting, new MediaOutput(canvasWidth, canvasHeight, canvasRate), output, cancellationToken)
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
                next.AdaptiveQuality = AdaptiveOf(videoSetting, quality);

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
                        cancellationToken,
                        yieldWhen: () => _takeovers.HasPending(videoLiveHistoryPkid))
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
                    // The canvas makes way the way a video does (see StreamVideoAsync), and comes
                    // back from the point it handed over at, with every source opened again.
                    await session.HandOverAsync().ConfigureAwait(false);
                    var picture = session.Output;
                    var pausedPosition = session.ContinuationMilliseconds;
                    if (pausedPosition <= 0 && resumeFrom > TimeSpan.Zero)
                    {
                        pausedPosition = (long)resumeFrom.TotalMilliseconds;
                    }

                    pausedPosition = Math.Max(0, pausedPosition);
                    foreach (var row in rows)
                    {
                        row.LastTimeStampBeforeStop = pausedPosition;
                        row.LiveStatus = LiveStatus.Live;
                        _videoRepository.Update(row);
                    }

                    _sessions.Remove(videoKey);
                    await session.DisposeAsync().ConfigureAwait(false);
                    _frames.Forget(videoKey);
                    session = null;

                    var program = new PausedProgram(
                        videoLiveHistoryPkid, videoKey, baseRow.VideoPath, pausedPosition, () => videoSetting, outputUrl, output, picture);
                    if (await RunTakeoversAsync(program, cancellationToken).ConfigureAwait(false) == StreamOutcome.Stopped)
                    {
                        MarkRows(rows, LiveStatus.Stopped, program.StopReason ?? _localizer.PrintMessage("live.stopped"));
                        return;
                    }

                    resumeFrom = TimeSpan.FromMilliseconds(pausedPosition);
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
    /// Plays what was asked to go on air in place of the program, one after the other, and answers
    /// once the program can come back. Each one goes out on the connection of the live and under
    /// the key of its program, so the preview keeps showing what is on air and a stop asked for the
    /// live reaches it. Stopped when the live was stopped meanwhile: the stop is already recorded
    /// on the program, at the position the program was left at.
    /// </summary>
    private async Task<StreamOutcome> RunTakeoversAsync(PausedProgram program, CancellationToken cancellationToken)
    {
        try
        {
            while (_takeovers.TryTake(program.History, out var takeover))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = takeover.Kind == TakeoverKind.Image
                    ? await StreamImageTakeoverAsync(takeover, program, cancellationToken).ConfigureAwait(false)
                    : await StreamVideoTakeoverAsync(takeover, program, cancellationToken).ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    return StreamOutcome.Stopped;
                }
            }

            return StreamOutcome.Finished;
        }
        finally
        {
            _takeovers.BackToProgram(program.History);
        }
    }

    /// <summary>
    /// A clip in place of the program - a spot, the video of a scene button - played once, from its
    /// start to its end. It ends sooner when something else is switched to, or when the user goes
    /// back to the live; either way the program comes back after it.
    /// </summary>
    private async Task<StreamOutcome> StreamVideoTakeoverAsync(
        Takeover takeover, PausedProgram program, CancellationToken cancellationToken)
    {
        var videoKey = program.DrivingKey;
        FfmpegStreamingSession? session = null;
        try
        {
            var probe = await _probe.ProbeAsync(takeover.Path, cancellationToken).ConfigureAwait(false);
            var setting = program.Setting();
            SayOnAir(takeover, program);
            _logger.LogInformation("Streaming {Path} in place of the live to {OutputUrl}", takeover.Path, program.OutputUrl);

            var resumeFrom = TimeSpan.Zero;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quality = await QualityOfAsync(setting, PictureOf(setting, probe, program.Output), program.Output, cancellationToken)
                    .ConfigureAwait(false);
                var next = FfmpegStreamingSession.Start(
                    _locator,
                    videoKey,
                    takeover.Path,
                    program.OutputUrl,
                    setting,
                    probe,
                    _logger,
                    resumeFrom,
                    _frames.PathOf(videoKey),
                    output: program.Output,
                    quality: quality,
                    alwaysSound: true);

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                // A clip is short: it plays out at the bitrate it started at, and the program after
                // it goes out at whatever the ladder decided meanwhile.
                var outcome = await MonitorAsync(
                        session,
                        videoKey,
                        program.History,
                        program.Path,
                        () => { },
                        cancellationToken,
                        adaptBitrate: false,
                        yieldWhen: () => _takeovers.ShouldEnd(program.History, takeover),
                        stopPosition: program.Position)
                    .ConfigureAwait(false);

                switch (outcome)
                {
                    case StreamOutcome.Stopped:
                        program.StopReason = session.StopReason;
                        return StreamOutcome.Stopped;

                    case StreamOutcome.Yielded:
                        // Cut by what comes next, or by the user going back to the live: what the
                        // relay holds of it still goes on air, and the next one follows it.
                        await session.HandOverAsync().ConfigureAwait(false);
                        return StreamOutcome.Finished;

                    case StreamOutcome.Reconfigured:
                        // A parameter changed from the preview: the clip carries on from where it got to.
                        if (session.SharesOutput)
                        {
                            await session.HandOverAsync().ConfigureAwait(false);
                        }

                        resumeFrom = TimeSpan.FromMilliseconds(session.ContinuationMilliseconds);
                        continue;

                    default:
                        // Played to its end, or broken: either way the program comes back.
                        await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        return StreamOutcome.Finished;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed streaming {Path} in place of the live", takeover.Path);
            return StreamOutcome.Finished;
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
    /// A picture in place of the program - a "be right back", a banner - on a canvas the size the
    /// program was going out at, fitted whole onto it. A still is decoded once and held, an
    /// animated one is played again whenever it ends (see OverlayMedia), and a silent track goes
    /// with it. It has no end of its own: it stays until the user resumes the live or switches to
    /// something else, and a pass that breaks meanwhile is reconnected like the program would be.
    /// </summary>
    private async Task<StreamOutcome> StreamImageTakeoverAsync(
        Takeover takeover, PausedProgram program, CancellationToken cancellationToken)
    {
        var videoKey = program.DrivingKey;
        FfmpegStreamingSession? session = null;
        var reconnects = new ReconnectBudget();
        try
        {
            var (width, height, media) = await ImageOfAsync(takeover.Path, cancellationToken).ConfigureAwait(false);
            var setting = program.Setting();
            var canvas = CanvasOf(program, setting);
            var (x, y, tileWidth, tileHeight) = FfmpegCommandBuilder.Contain(width, height, canvas.Width, canvas.Height);
            FfmpegCompositionItem[] items =
            [
                new(SourceKind.Overlay, takeover.Path, x, y, tileWidth, tileHeight, AudioEnabled: false, Overlay: media)
            ];

            SayOnAir(takeover, program);
            _logger.LogInformation(
                "Streaming the picture {Path} in place of the live, at {Width}x{Height}", takeover.Path, canvas.Width, canvas.Height);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quality = await QualityOfAsync(setting, canvas, program.Output, cancellationToken).ConfigureAwait(false);
                var next = FfmpegStreamingSession.StartComposition(
                    _locator,
                    videoKey,
                    items,
                    program.OutputUrl,
                    setting,
                    canvas.Width,
                    canvas.Height,
                    canvas.FrameRate,
                    _logger,
                    previewPath: _frames.PathOf(videoKey),
                    output: program.Output,
                    quality: quality,
                    alwaysSound: true);
                next.AdaptiveQuality = AdaptiveOf(setting, quality);

                var previous = session;
                session = next;
                _sessions.Register(next);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                var outcome = await MonitorAsync(
                        session,
                        videoKey,
                        program.History,
                        program.Path,
                        () => { },
                        cancellationToken,
                        yieldWhen: () => _takeovers.ShouldEnd(program.History, takeover),
                        stopPosition: program.Position)
                    .ConfigureAwait(false);

                if (outcome == StreamOutcome.Stopped)
                {
                    program.StopReason = session.StopReason;
                    return StreamOutcome.Stopped;
                }

                if (outcome == StreamOutcome.Yielded)
                {
                    await session.HandOverAsync().ConfigureAwait(false);
                    return StreamOutcome.Finished;
                }

                if (outcome == StreamOutcome.Reconfigured)
                {
                    if (session.SharesOutput)
                    {
                        await session.HandOverAsync().ConfigureAwait(false);
                    }

                    continue;
                }

                // A picture never ends by itself: the pass broke. It goes back on air for as long
                // as it was asked to stay, within the reconnections a live is allowed.
                await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (_takeovers.ShouldEnd(program.History, takeover)
                    || !await ShouldReconnectAsync(
                            session,
                            reconnects,
                            takeover.Label,
                            message => SaveMessageOnVideoLiveHistory(message, program.History, program.Path, LiveStatus.Live, null),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return StreamOutcome.Finished;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed streaming the picture {Path} in place of the live", takeover.Path);
            return StreamOutcome.Finished;
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
    /// How big a picture is and how ffmpeg opens it. One ffprobe cannot read is laid over the whole
    /// frame as a still, so that the error the pass ends on, if it does, is ffmpeg's own about it.
    /// </summary>
    private async Task<(int Width, int Height, OverlayMedia Media)> ImageOfAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            return (probe.Width, probe.Height, OverlayMedia.Of(probe));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not probe the picture {Path}", path);
            return (0, 0, OverlayMedia.Still);
        }
    }

    /// <summary>
    /// The picture a takeover goes out at: the one its program was going out at, so that going to
    /// it and back is not a change of format; the one of the connection when the program had not
    /// started yet; otherwise the size of the setting, or a 1080p frame.
    /// </summary>
    private static MediaOutput CanvasOf(PausedProgram program, VideoSettingEntity setting)
    {
        var rate = setting.FrameRate is > 0 ? setting.FrameRate.Value : DefaultCanvasFrameRate;
        if ((program.Picture ?? program.Output?.Frame) is { Width: > 0, Height: > 0 } picture)
        {
            return new MediaOutput(
                FfmpegCommandBuilder.Even(picture.Width),
                FfmpegCommandBuilder.Even(picture.Height),
                picture.FrameRate > 0 ? picture.FrameRate : rate);
        }

        return setting.VideoWidth is > 0 && setting.VideoHeight is > 0
            ? new MediaOutput(FfmpegCommandBuilder.Even(setting.VideoWidth.Value), FfmpegCommandBuilder.Even(setting.VideoHeight.Value), rate)
            : new MediaOutput(1920, 1080, rate);
    }

    /// <summary>What the row of the program says while something else is on air in its place.</summary>
    private void SayOnAir(Takeover takeover, PausedProgram program)
    {
        var message = takeover.ButtonPkid is null
            ? _localizer.PrintMessage("video.live.spot.playing", [takeover.Label])
            : _localizer.PrintMessage("video.live.scene.playing", [takeover.Label]);
        _logger.LogInformation("{Message}", message);
        SaveMessageOnVideoLiveHistory(message, program.History, program.Path, LiveStatus.Live, null);
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
    /// The picture a pass is encoded at: the one of the live on a connection that keeps one format
    /// (YouTube, Kick, Facebook Gaming) - a short of 432x208 on a 1080p live costs what a 1080p
    /// frame costs - or its own everywhere else.
    /// </summary>
    private static MediaOutput PictureOf(VideoSettingEntity setting, MediaProbeResult probe, LiveOutput? output)
    {
        var own = FfmpegCommandBuilder.ResolveOutput(setting, probe);
        return output is { Profile.UniformFormat: true }
            ? output.Frame ?? FfmpegCommandBuilder.FrameOfLive(setting, own)
            : own;
    }

    /// <summary>
    /// The encoder level of a pass: the one of the setting, or the one this machine keeps up with
    /// at this size when the setting leaves it to the machine. Measured once, then known.
    /// </summary>
    private async Task<EncoderQuality> QualityOfAsync(
        VideoSettingEntity setting, MediaOutput output, LiveOutput? live, CancellationToken cancellationToken)
    {
        var quality = await _tuning.ResolveAsync(setting, output.Width, output.Height, output.FrameRate, cancellationToken)
            .ConfigureAwait(false);

        // Under the cap of a live whose encoder was found not to keep up, when the machine decides.
        return live is not null && VideoSettingQuality.Of(setting) == EncoderQuality.Auto ? live.Cap(quality) : quality;
    }

    /// <summary>The level a pass may be adapted from: the machine's choice, never the user's.</summary>
    private static EncoderQuality? AdaptiveOf(VideoSettingEntity setting, EncoderQuality quality) =>
        VideoSettingQuality.Of(setting) == EncoderQuality.Auto ? quality : null;

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
    /// canvas of two or more pictures turns them on for itself. Its overlays do not count: a still
    /// is decoded once for the whole live, and a video with a logo on it is still one video.</para>
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
        if (VideoSettingLatency.IsOn(setting) || rows.Count(row => row.SourceKind.HasPicture() && !row.SourceKind.IsOverlay()) < 2)
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
        /// <summary>Something goes on air in place of the pass (see LiveTakeovers): it hands over where it got to.</summary>
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
    /// <param name="adaptBitrate">Whether a network that does not carry the live restarts the pass at a lower bitrate.</param>
    /// <param name="yieldWhen">Whether the pass is to make way for what goes on air in its place (see LiveTakeovers).</param>
    /// <param name="stopPosition">
    /// Where a stop leaves the row of <paramref name="inputPath"/>: the position of this pass when
    /// null, the point the program was left at for a pass that stands in for it.
    /// </param>
    private async Task<StreamOutcome> MonitorAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath,
        Action onAir,
        CancellationToken cancellationToken,
        bool adaptBitrate = true,
        Func<bool>? yieldWhen = null,
        long? stopPosition = null)
    {
        var announced = false;
        var warnedSlow = false;
        while (true)
        {
            if (session.StopRequested)
            {
                await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath, stopPosition).ConfigureAwait(false);
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
                await StopAndRecordAsync(session, videoKey, videoLiveHistoryPkid, inputPath, stopPosition).ConfigureAwait(false);
                return StreamOutcome.Stopped;
            }

            if (session.RestartRequested)
            {
                return StreamOutcome.Reconfigured;
            }

            if (yieldWhen?.Invoke() == true)
            {
                return StreamOutcome.Yielded;
            }

            // The end of a canvas: its last frame starts one frame before it, so a position that
            // waited for the duration itself never got there - the canvas has no end of its own
            // (the blank frame under it runs for ever), and the live sat still until the stall
            // timeout called it broken and reconnected it for nothing.
            var durationMs = session.Probe.DurationSeconds > 0 ? (long)(session.Probe.DurationSeconds * 1000) : 0;
            var lastFrameMs = (long)Math.Ceiling(1000d / Math.Max(1d, session.Output.FrameRate)) + LastFrameSlackMilliseconds;
            if (durationMs > 0 && session.ContinuationMilliseconds >= durationMs - lastFrameMs)
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

                // An encoder that produces the live no faster than it goes out has no margin left
                // for a slow moment of the machine: the live is carried on one level lighter, from
                // where it is, rather than stuttering at the level it cannot hold.
                if (session.BehindFor > AdaptAfter
                    && session.AdaptiveQuality is { } level
                    && session.Connection?.TryLower(level, out var lighter) == true)
                {
                    _logger.LogWarning(
                        "The encoder of the live to {Platform} does not keep ahead at {Level}: carrying on at {Lighter}",
                        profile.Platform,
                        level,
                        lighter);
                    return StreamOutcome.Reconfigured;
                }

                if (!warnedSlow && session.BehindFor > AdaptAfter)
                {
                    // Nothing lighter to go to, or a level the user chose: said once, so the log
                    // explains the stutters instead of the live going quiet about them.
                    warnedSlow = true;
                    _logger.LogWarning(
                        "The encoder of the live to {Platform} does not keep ahead of it: this machine is at its limit for this live (lighter sources, a lower resolution or a GPU encoder would help)",
                        profile.Platform);
                }

                // The other way a live falls behind: the encoder keeps ahead and the network does
                // not take what it makes. The live goes on at the bitrate the network was measured
                // to carry, from where it is, and climbs back once the network carried it cleanly
                // for a while (BitrateLadder). A spot is short: it plays out as it started, and the
                // pass after it goes out at whatever the ladder decided meanwhile.
                if (adaptBitrate && session.AdaptBitrate() is { } rate)
                {
                    if (rate.Lowers)
                    {
                        _logger.LogWarning(
                            "The network to {Platform} carries {Throughput} kbps, less than the live: carrying on at {To} kbps of video instead of {From}",
                            profile.Platform,
                            rate.Throughput / 1000,
                            rate.To / 1000,
                            rate.From / 1000);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "The live to {Platform} went out clean at {From} kbps of video: trying {To}",
                            profile.Platform,
                            rate.From / 1000,
                            rate.To / 1000);
                    }

                    return StreamOutcome.Reconfigured;
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
        StreamPlatform.Kick => "Kick",
        StreamPlatform.Facebook => "Facebook Gaming",
        _ => "RTMP"
    };

    private async Task StopAndRecordAsync(
        FfmpegStreamingSession session,
        int videoKey,
        long videoLiveHistoryPkid,
        string inputPath,
        long? position = null)
    {
        await session.StopAsync().ConfigureAwait(false);

        var message = session.StopReason ?? _localizer.PrintMessage("live.stopped");
        _logger.LogInformation("{Message}", message);
        if (videoKey > 0)
        {
            _videoRepository.SetStopFlag(videoKey, false);
            SaveMessageOnVideoLiveHistory(
                message, videoLiveHistoryPkid, inputPath, LiveStatus.Stopped, null, position ?? session.PositionMilliseconds);
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
    /// The program of a live while something else is on air in its place: where the live goes, and
    /// what has to be known to bring the program back where it was left.
    /// </summary>
    /// <param name="History">The live, as the switchboard knows it.</param>
    /// <param name="DrivingKey">The row the live runs under: the takeover runs under it too.</param>
    /// <param name="Path">The file of the row a stop is recorded on.</param>
    /// <param name="Position">Where the program was left, which is where a stop leaves it.</param>
    /// <param name="Setting">The setting the program goes on air with, read when something has to go on air.</param>
    /// <param name="Picture">The picture the program was going out at, when it had started.</param>
    private sealed record PausedProgram(
        long History,
        int DrivingKey,
        string Path,
        long Position,
        Func<VideoSettingEntity> Setting,
        string OutputUrl,
        LiveOutput? Output,
        MediaOutput? Picture)
    {
        /// <summary>Why the live was stopped while it stood in for the program, when it was not the user.</summary>
        public string? StopReason { get; set; }
    }

    /// <summary>
    /// Rows that are streamed together: the files of a playlist, or the sources of one canvas.
    /// <see cref="ScenePkid"/> is what tells them apart.
    /// </summary>
    private sealed record VideoGroup(long? ScenePkid, List<VideoEntity> Videos);
}

/// <summary>Abstraction of the playlist streamer, so the services can be unit tested without ffmpeg.</summary>
public interface IVideoPlaylistStreamer
{
    /// <param name="transport">What publishes to the ingest, when the channel asks for it; null is the platform default.</param>
    Task StreamPlaylistAsync(
        IReadOnlyList<VideoEntity> videos,
        string outputUrl,
        long videoLiveHistoryPkid,
        RelayTransport? transport,
        CancellationToken cancellationToken);
}
