using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Port of the frame loop of <c>StreamService#startVideoStreaming</c>: it streams the videos of a
/// live history one after another, mirrors every state change on the video row and honours the
/// "should be stopped" flag that <c>/live/stop-live</c> sets.
/// </summary>
public sealed class FfmpegVideoPlaylistStreamer : IVideoPlaylistStreamer
{
    private static readonly TimeSpan StopFlagPollInterval = TimeSpan.FromMilliseconds(500);

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
        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StreamVideoAsync(video, outputUrl, videoLiveHistoryPkid, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StreamVideoAsync(
        VideoEntity video,
        string outputUrl,
        long videoLiveHistoryPkid,
        CancellationToken cancellationToken)
    {
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

            var probe = await _probe.ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "FORMAT: of grabber on running machine = {PixelFormat}", FfmpegCodecCatalog.ResolvePixelFormat(videoSetting.PixelFormat));

            await using var session = FfmpegStreamingSession.Start(
                _locator, videoKey, inputPath, outputUrl, videoSetting, probe, _logger);

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
                SaveMessageOnVideoLiveHistory(endedMessage, videoLiveHistoryPkid, inputPath, LiveStatus.Ended, null);
                return;
            }

            var streamingError = _localizer.PrintMessage(
                "error.during.streaming.video", [inputPath, videoLiveHistoryPkid])
                + "\n"
                + (string.IsNullOrWhiteSpace(errorOutput) ? $"ffmpeg exited with code {exitCode}" : errorOutput.Trim());
            _logger.LogError("{Message}", streamingError);
            SaveMessageOnVideoLiveHistory(streamingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now);
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
            SaveMessageOnVideoLiveHistory(startingError, videoLiveHistoryPkid, inputPath, LiveStatus.Error, DateTime.Now);
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
        SaveMessageOnVideoLiveHistory(message, videoLiveHistoryPkid, inputPath, LiveStatus.Stopped, null);
        return true;
    }

    /// <summary>Port of <c>saveMessageOnVideoLiveHistory</c>.</summary>
    private void SaveMessageOnVideoLiveHistory(
        string message,
        long videoLiveHistoryPkid,
        string inputPath,
        LiveStatus liveStatus,
        DateTime? dateTime)
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

        video.Message = message;
        video.LiveStatus = liveStatus;
        _videoRepository.Update(video);

        // This is the only place where the engine changes what a row shows, so it is also the
        // only place that has to tell the open pages that the row they drew is out of date.
        _notifier.Raise();
    }
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
