using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.service.StreamService</c>.</summary>
public sealed class StreamingService
{
    /// <summary>The original project hardcoded the user of every live history.</summary>
    public const string CurrentUserName = "Mario";

    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly VideoLiveHistoryRepository _videoLiveHistoryRepository;
    private readonly SceneRepository _sceneRepository;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly BackgroundTaskExecutor _executor;
    private readonly IVideoPlaylistStreamer _streamer;
    private readonly StreamingSessionRegistry _sessions;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<StreamingService> _logger;

    public StreamingService(
        VideoRepository videoRepository,
        VideoSettingRepository videoSettingRepository,
        VideoLiveHistoryRepository videoLiveHistoryRepository,
        SceneRepository sceneRepository,
        ResponseFactory responses,
        Localizer localizer,
        BackgroundTaskExecutor executor,
        IVideoPlaylistStreamer streamer,
        StreamingSessionRegistry sessions,
        LiveChangeNotifier notifier,
        ILogger<StreamingService> logger)
    {
        _videoRepository = videoRepository;
        _videoSettingRepository = videoSettingRepository;
        _videoLiveHistoryRepository = videoLiveHistoryRepository;
        _sceneRepository = sceneRepository;
        _responses = responses;
        _localizer = localizer;
        _executor = executor;
        _streamer = streamer;
        _sessions = sessions;
        _notifier = notifier;
        _logger = logger;
    }

    public MessageResponse StartLive(StartLiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelName = request.ChannelName!;
        var platformStreamName = request.PlatformStreamName!;
        var videoPathFolder = NormalizeUserPath(request.VideoPath!);
        var streamKey = request.StreamKey!;
        var streamUrl = request.StreamUrl!;

        CheckIfALiveAlreadyStreamingForAChannel(channelName, platformStreamName);

        // Checked before any row is saved: ffprobe's own error on a missing file is unreadable.
        if (!File.Exists(videoPathFolder) && !Directory.Exists(videoPathFolder))
        {
            _logger.LogError("{Message} {Path}", _localizer.PrintMessage("file.not.found"), videoPathFolder);
            throw new NotFoundCustomException("file.not.found");
        }

        var timeStartLive = DateTime.Now;
        SaveVideoLiveHistory(videoPathFolder, timeStartLive, streamUrl, streamKey, platformStreamName);

        var videoLiveHistory = RetrievedVideoLiveHistorySaved(videoPathFolder, timeStartLive);

        SaveVideoPaths(videoPathFolder, videoLiveHistory, request.VideoSettingsRecord, channelName);

        var streamingUrl = FfmpegCommandBuilder.BuildStreamingUrl(streamUrl, streamKey);
        return StreamingVideo(videoLiveHistory, streamingUrl);
    }

    /// <summary>
    /// A live from a folder picked in the playlist wizard. Unlike <see cref="StartLive"/>, which also
    /// takes a single file, the path has to be a folder: its videos (and nothing else lying in it)
    /// become the playlist, one row each, in the order Explorer lists them.
    /// </summary>
    public MessageResponse StartPlaylist(StartLiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var folder = NormalizeUserPath(request.VideoPath ?? string.Empty);
        if (folder.Length == 0 || File.Exists(folder))
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("playlist.not.a.folder", [folder]));
            throw new NotFoundCustomException("playlist.not.a.folder", [folder]);
        }

        if (!Directory.Exists(folder))
        {
            _logger.LogError("{Message} {Path}", _localizer.PrintMessage("folder.not.found"), folder);
            throw new NotFoundCustomException("folder.not.found");
        }

        return StartLive(request with { VideoPath = folder });
    }

    /// <summary>
    /// Port of <c>startVideo</c>: replays or restarts a single video whose details and settings
    /// are already stored, so no video row is created here.
    /// </summary>
    public MessageResponse StartVideo(VideoRequest videoRecord)
    {
        ArgumentNullException.ThrowIfNull(videoRecord);

        var startLiveRecord = MapToStartLiveRecord(videoRecord);

        var channelName = startLiveRecord.ChannelName!;
        var platformStreamName = startLiveRecord.PlatformStreamName!;
        CheckIfALiveAlreadyStreamingForAChannel(channelName, platformStreamName);

        var videoPathFolder = startLiveRecord.VideoPath!;
        var streamKey = startLiveRecord.StreamKey!;
        var streamUrl = startLiveRecord.StreamUrl!;

        var timeStartLive = DateTime.Now;
        SaveVideoLiveHistory(videoPathFolder, timeStartLive, streamUrl, streamKey, platformStreamName);

        // The Java version rebuilt the history from the request payload instead of using the row
        // that was just inserted; the behaviour is preserved on purpose.
        var videoLiveHistory = videoRecord.VideoLiveHistory?.ToEntity() ?? new VideoLiveHistoryEntity();

        var streamingUrl = FfmpegCommandBuilder.BuildStreamingUrl(streamUrl, streamKey);
        return StreamingVideo(videoLiveHistory, streamingUrl);
    }

    public void StopVideoStreamingByPkid(int videoLivePkid)
    {
        var video = CheckIfExistsAndReturnEntity(videoLivePkid);
        video.ShouldBeStop = true;
        SaveFlagToStopLive(video);
        _notifier.Raise();

        // The row flag is what the streaming loop polls; signalling the process makes the stop
        // immediate for the user interface too.
        if (_sessions.TryGet(videoLivePkid, out var session) && session is not null)
        {
            _ = session.StopAsync();
        }
    }

    public void ResetFlag(int videoLivePkid)
    {
        var video = CheckIfExistsAndReturnEntity(videoLivePkid);
        video.ShouldBeStop = false;
        SaveFlagToStopLive(video);
    }

    /// <summary>
    /// Forgets where every video of a live history was stopped, so the next play starts the
    /// playlist from the first one. This is what the restart button asks for, and what a play does
    /// by itself when the interrupted pass has nothing left to stream.
    /// </summary>
    public void RestartFromBeginning(long videoLiveHistoryPkid)
    {
        foreach (var video in _videoRepository.FindByLiveHistoryId(videoLiveHistoryPkid))
        {
            // The status goes back too: an ENDED left over from the previous pass would make the
            // playlist read as finished while it is starting over.
            if (video.LastTimeStampBeforeStop == 0 && video.LiveStatus is LiveStatus.Offline or LiveStatus.Live)
            {
                continue;
            }

            video.LastTimeStampBeforeStop = 0;
            if (video.LiveStatus != LiveStatus.Live)
            {
                video.LiveStatus = LiveStatus.Offline;
            }

            _videoRepository.Update(video);
        }

        _notifier.Raise();
    }

    /// <summary>
    /// "Start again from this video" of the playlist dialog: the videos before it count as streamed
    /// (so a later play resumes after them, not from the first one), it and the ones after it
    /// start over. The play itself is <see cref="StartVideo"/>, which then finds only these to stream.
    /// </summary>
    public void PrepareStartFrom(int pkid)
    {
        var target = CheckIfExistsAndReturnEntity(pkid);
        if (target.VideoLiveHistoryId is not { } historyPkid)
        {
            throw new NotFoundCustomException("video.history.not.found");
        }

        // Checked before a row is touched: a refused start must leave the playlist as it was.
        CheckIfALiveAlreadyStreamingForAChannel(target.ChannelName, GetPlatformStreamName(historyPkid));

        var skipped = _localizer.PrintMessage("video.live.skipped", [target.Name]);
        foreach (var video in _videoRepository.FindByLiveHistoryId(historyPkid).Where(video => video.ScenePkid is null))
        {
            if (video.Pkid < target.Pkid)
            {
                if (video.LiveStatus == LiveStatus.Ended && video.LastTimeStampBeforeStop > 0)
                {
                    continue;
                }

                // ENDED with a position is what the planner reads as "streamed through": the
                // position only has to be there, the status is what makes it skip the file.
                video.LiveStatus = LiveStatus.Ended;
                video.LastTimeStampBeforeStop = Math.Max(video.LastTimeStampBeforeStop, SkippedPosition);
                video.Message = skipped;
            }
            else
            {
                video.LiveStatus = LiveStatus.Offline;
                video.LastTimeStampBeforeStop = 0;
            }

            _videoRepository.Update(video);
        }

        _notifier.Raise();
    }

    /// <summary>The position a skipped video is left at: any value above zero, see <see cref="PrepareStartFrom"/>.</summary>
    private const long SkippedPosition = 1;

    public Task StopAllAsync() => _sessions.StopAllAsync();

    public void Shutdown() => _shutdown.Cancel();

    private void CheckIfALiveAlreadyStreamingForAChannel(string channelName, string platformStreamName)
    {
        var videoList = _videoRepository.FindByLiveStatusAndChannelName(LiveStatus.Live, channelName);
        if (videoList.Count == 0)
        {
            return;
        }

        var alreadyStreaming = videoList
            .Where(video => video.VideoLiveHistoryId is not null
                            && GetPlatformStreamName(video.VideoLiveHistoryId.Value)
                                .Equals(platformStreamName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (alreadyStreaming.Count == 0)
        {
            return;
        }

        throw new LiveException("channel.has.already.a.live.active", [channelName]);
    }

    private string GetPlatformStreamName(long videoLiveHistoryPkid) =>
        _videoLiveHistoryRepository.FindByPkid(videoLiveHistoryPkid)?.PlatformStreamName ?? string.Empty;

    /// <summary>
    /// Starts a live from a canvas instead of a folder. The scene is written out as one video row
    /// per source, exactly the way a folder scan writes one row per file: everything downstream
    /// (the live page, the history, the stop, the preview) reads rows and never learns that a
    /// canvas and a playlist are not the same thing.
    /// </summary>
    public MessageResponse StartSceneLive(StartSceneLiveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelName = request.ChannelName!;
        var platformStreamName = request.PlatformStreamName!;
        var streamKey = request.StreamKey!;
        var streamUrl = request.StreamUrl!;

        CheckIfALiveAlreadyStreamingForAChannel(channelName, platformStreamName);

        var scene = _sceneRepository.FindByPkid(request.ScenePkid)
            ?? throw new NotFoundCustomException("scene.not.found", [request.ScenePkid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var sources = scene.Items.Where(item => item.SourceKind.HasPicture() || item.AudioEnabled).ToList();
        if (!sources.Any(item => item.SourceKind.HasPicture()))
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("scene.no.picture", [scene.Name]));
            throw new NotFoundCustomException("scene.no.picture", [scene.Name]);
        }

        // The folder column is what the history is looked up by on a restart, so a scene is stored
        // as a name of its own rather than as a path that does not exist on disk.
        var source = SceneReference.Of(scene);

        var timeStartLive = DateTime.Now;
        SaveVideoLiveHistory(source, timeStartLive, streamUrl, streamKey, platformStreamName);
        var videoLiveHistory = RetrievedVideoLiveHistorySaved(source, timeStartLive);

        // Saved the way a folder start saves it: every row keeps the setting it went on air with, so
        // a play or a restart of this live later reads it from the row and needs nothing else.
        var videoSettingId = PersistVideoSetting(request.VideoSettingsRecord?.ToEntity());
        SaveSceneSources(scene, sources, videoLiveHistory, videoSettingId, channelName);

        var streamingUrl = FfmpegCommandBuilder.BuildStreamingUrl(streamUrl, streamKey);
        return StreamingVideo(videoLiveHistory, streamingUrl);
    }

    private void SaveSceneSources(
        SceneEntity scene,
        IReadOnlyList<SceneItemEntity> items,
        VideoLiveHistoryEntity videoLiveHistory,
        int? videoSettingId,
        string channelName)
    {
        foreach (var item in items)
        {
            var video = new VideoEntity
            {
                Name = string.IsNullOrWhiteSpace(item.Label) ? DescribeSource(item) : item.Label!,
                // A file keeps its path here, the way every row the folder scan wrote does; a device
                // has no path, so the tile carries the name the pages show and the target the
                // command line opens.
                VideoPath = item.SourceKind == SourceKind.File
                    ? Path.GetFullPath(StreamingService.NormalizeUserPath(item.SourceTarget))
                    : SceneReference.ItemPath(item.SourceKind, item.SourceTarget),
                Extension = item.SourceKind == SourceKind.File
                    ? ExtractExtensionFile(Path.GetFileName(item.SourceTarget))
                    : item.SourceKind.ToWireValue().ToLowerInvariant(),
                LastTimeStampBeforeStop = 0L,
                LiveStatus = LiveStatus.Offline,
                VideoLiveHistoryId = videoLiveHistory.Pkid,
                ShouldBeStop = false,
                StartDateLive = DateTime.Now,
                ChannelName = channelName,
                VideoSettingId = videoSettingId,
                SourceKind = item.SourceKind,
                SourceTarget = item.SourceKind == SourceKind.File ? null : item.SourceTarget,
                ScenePkid = scene.Pkid,
                X = item.X,
                Y = item.Y,
                Width = item.Width,
                Height = item.Height,
                AudioEnabled = item.AudioEnabled
            };

            _videoRepository.Insert(video);
        }

        _notifier.Raise();
    }

    private static string DescribeSource(SceneItemEntity item) => item.SourceKind switch
    {
        SourceKind.Screen => item.SourceTarget,
        SourceKind.Camera => item.SourceTarget.Replace("video=", string.Empty, StringComparison.Ordinal),
        SourceKind.Microphone => item.SourceTarget.Replace("audio=", string.Empty, StringComparison.Ordinal),
        _ => Path.GetFileName(item.SourceTarget)
    };

    private MessageResponse StreamingVideo(VideoLiveHistoryEntity videoLiveHistory, string streamingUrl)
    {
        var videoLiveHistoryId = videoLiveHistory.Pkid;

        var videoList = _videoRepository.FindByLiveHistoryId(videoLiveHistoryId);
        if (videoList.Count == 0)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("video.streaming.not.found"));
            throw new NotFoundCustomException("video.streaming.not.found");
        }

        _executor.Execute(() => _ = RunPlaylistAsync(videoList, streamingUrl, videoLiveHistoryId));

        return _responses.Build("live.started", StatusCodes.Status202Accepted);
    }

    private async Task RunPlaylistAsync(IReadOnlyList<VideoEntity> videos, string streamingUrl, long videoLiveHistoryId)
    {
        try
        {
            await _streamer.StreamPlaylistAsync(videos, streamingUrl, videoLiveHistoryId, _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Streaming of history {VideoLiveHistoryId} cancelled", videoLiveHistoryId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Streaming of history {VideoLiveHistoryId} failed", videoLiveHistoryId);
        }
    }

    private VideoEntity CheckIfExistsAndReturnEntity(int pkid)
    {
        var video = _videoRepository.FindByPkid(pkid);
        if (video is null)
        {
            _logger.LogWarning("{Message}", _localizer.PrintMessage("video.not.found"));
            throw new NotFoundCustomException("video.not.found");
        }

        return video;
    }

    private void SaveFlagToStopLive(VideoEntity video) => _videoRepository.Update(video);

    private VideoLiveHistoryEntity RetrievedVideoLiveHistorySaved(string videoFileAbsolutePath, DateTime timeStartLive)
    {
        var history = _videoLiveHistoryRepository.FindByFolderOfVideoToStreamAndLocalDateTimeStartLive(
            videoFileAbsolutePath, timeStartLive);

        if (history is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("video.history.not.found"));
            throw new NotFoundCustomException("video.history.not.found");
        }

        return history;
    }

    private void SaveVideoLiveHistory(
        string videoFileAbsolutePath,
        DateTime zoneIdTime,
        string streamUrl,
        string streamKey,
        string platformStreamingName)
    {
        _videoLiveHistoryRepository.Insert(new VideoLiveHistoryEntity
        {
            UserName = CurrentUserName,
            FolderOfVideoToStream = videoFileAbsolutePath,
            LocalDateTimeStartLive = zoneIdTime,
            StreamUrl = streamUrl,
            StreamKey = streamKey,
            PlatformStreamName = platformStreamingName
        });
    }

    /// <summary>
    /// Windows "Copy as path" wraps the path in double quotes: without stripping them
    /// <see cref="Path.GetFullPath(string)"/> sees a relative path and prefixes the working directory.
    /// </summary>
    internal static string NormalizeUserPath(string path) => path.Trim().Trim('"').Trim();

    private void SaveVideoPaths(
        string videoPathFolder,
        VideoLiveHistoryEntity videoLiveHistory,
        VideoSettingsRequest? videoSettingsRecord,
        string channelName)
    {
        // The rows exist from here on: the pages showing them are already out of date.
        // A null record means "no configuration", exactly like the MapStruct mapper returning null.
        var videoSetting = videoSettingsRecord?.ToEntity();

        if (Directory.Exists(videoPathFolder))
        {
            SaveAllVideoPaths(videoPathFolder, videoLiveHistory, videoSetting, channelName);
        }
        else
        {
            SaveOneVideoPaths(videoPathFolder, videoLiveHistory, videoSetting, channelName);
        }

        _notifier.Raise();
    }

    private void SaveAllVideoPaths(
        string videoFile,
        VideoLiveHistoryEntity videoLiveHistory,
        VideoSettingEntity? videoSetting,
        string channelName)
    {
        var videoList = Directory
            .EnumerateFiles(videoFile)
            .Where(path =>
            {
                if (Directory.Exists(path))
                {
                    return false;
                }

                var fileName = Path.GetFileName(path);
                _logger.LogInformation("{Message}", _localizer.PrintMessage("file.name.found", [fileName]));

                var lastDotIndex = fileName.LastIndexOf('.');
                if (lastDotIndex == -1)
                {
                    _logger.LogError(
                        "{Message}", _localizer.PrintMessage("extension.not.found.with.param", [lastDotIndex]));
                    return false;
                }

                return VideoExtensions.IsVideoExtensionPresent(fileName[(lastDotIndex + 1)..]);
            })
            // The rows are streamed in pkid order, so the insert order is the playlist order: the
            // file system gives no order at all, and "Episode 10" belongs after "Episode 2".
            .Order(NaturalFileNameComparer.Instance)
            .ToList();

        if (videoList.Count == 0)
        {
            var absolutePath = Path.GetFullPath(videoFile);
            _logger.LogError("{Message}", _localizer.PrintMessage("folder.empty", [absolutePath]));
            throw new NotFoundCustomException("folder.empty", [absolutePath]);
        }

        foreach (var file in videoList)
        {
            SaveOnModelVideo(BuildVideo(file, videoLiveHistory, channelName), videoSetting);
        }
    }

    private void SaveOneVideoPaths(
        string videoFile,
        VideoLiveHistoryEntity videoLiveHistory,
        VideoSettingEntity? videoSetting,
        string channelName)
    {
        SaveOnModelVideo(BuildVideo(videoFile, videoLiveHistory, channelName), videoSetting);
    }

    private VideoEntity BuildVideo(
        string videoFile,
        VideoLiveHistoryEntity videoLiveHistory,
        string channelName)
    {
        var fullPath = Path.GetFullPath(videoFile);
        return new VideoEntity
        {
            Name = Path.GetFileName(fullPath),
            VideoPath = fullPath,
            Extension = ExtractExtensionFile(Path.GetFileName(fullPath)),
            LastTimeStampBeforeStop = 0L,
            LiveStatus = LiveStatus.Offline,
            VideoLiveHistoryId = videoLiveHistory.Pkid,
            ShouldBeStop = false,
            StartDateLive = DateTime.Now,
            ChannelName = channelName
        };
    }

    /// <summary>
    /// JPA cascaded the detached <c>VideoSetting</c> of the request: an already existing
    /// identifier was merged (updated), a new one was inserted. Every video of a folder got its
    /// own copy, so the option rows are never shared.
    /// </summary>
    private void SaveOnModelVideo(VideoEntity video, VideoSettingEntity? videoSetting)
    {
        video.VideoSettingId = PersistVideoSetting(videoSetting);
        video.Pkid = _videoRepository.Insert(video);
    }

    private int? PersistVideoSetting(VideoSettingEntity? setting)
    {
        if (setting is null)
        {
            return null;
        }

        if (setting.Id is not null && _videoSettingRepository.FindById(setting.Id.Value) is not null)
        {
            _videoSettingRepository.Update(setting);
            return setting.Id;
        }

        return _videoSettingRepository.Insert(CopyOf(setting));
    }

    private static VideoSettingEntity CopyOf(VideoSettingEntity source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        VideoCodec = source.VideoCodec,
        VideoCodecName = source.VideoCodecName,
        PixelFormat = source.PixelFormat,
        VideoBitrate = source.VideoBitrate,
        VideoFormat = source.VideoFormat,
        LastModified = source.LastModified,
        IsDefaultConfiguration = source.IsDefaultConfiguration,
        DefaultPlatformConfiguration = source.DefaultPlatformConfiguration,
        GopSize = source.GopSize,
        IsVideoAndAudioSettingActive = source.IsVideoAndAudioSettingActive,
        AudioSetting = source.AudioSetting is null
            ? null
            : new AudioSettingEntity
            {
                Id = source.AudioSetting.Id,
                AudioCodec = source.AudioSetting.AudioCodec,
                AudioBitrate = source.AudioSetting.AudioBitrate
            },
        VideoSettingsOptions = source.VideoSettingsOptions
            .Select(option => new VideoSettingsOptionEntity { Key = option.Key, Value = option.Value })
            .ToList()
    };

    private string ExtractExtensionFile(string? fileName)
    {
        if (fileName is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("extension.not.found"));
            throw new NotFoundCustomException("extension.not.found");
        }

        _logger.LogInformation("nome file{FileName}", fileName);

        var files = fileName.Split('.');
        if (files.Length < 2)
        {
            throw new FileReadingException("video.not.valid", [fileName]);
        }

        return files[^1];
    }

    private static StartLiveRequest MapToStartLiveRecord(VideoRequest videoRecord) => new(
        videoRecord.VideoLiveHistory?.StreamUrl,
        videoRecord.VideoLiveHistory?.StreamKey,
        videoRecord.VideoPath,
        videoRecord.VideoLiveHistory?.PlatformStreamName,
        videoRecord.ChannelName,
        videoRecord.VideoSetting);
}
