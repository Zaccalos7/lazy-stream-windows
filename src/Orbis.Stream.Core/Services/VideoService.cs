using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.service.VideoService</c>.</summary>
public sealed class VideoService
{
    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly VideoLiveHistoryRepository _videoLiveHistoryRepository;
    private readonly SceneRepository _sceneRepository;
    private readonly LiveHistoryCleanupService _cleanup;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly BackgroundTaskExecutor _executor;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<VideoService> _logger;

    public VideoService(
        VideoRepository videoRepository,
        VideoSettingRepository videoSettingRepository,
        VideoLiveHistoryRepository videoLiveHistoryRepository,
        SceneRepository sceneRepository,
        LiveHistoryCleanupService cleanup,
        ResponseFactory responses,
        Localizer localizer,
        BackgroundTaskExecutor executor,
        LiveChangeNotifier notifier,
        ILogger<VideoService> logger)
    {
        _videoRepository = videoRepository;
        _videoSettingRepository = videoSettingRepository;
        _videoLiveHistoryRepository = videoLiveHistoryRepository;
        _sceneRepository = sceneRepository;
        _cleanup = cleanup;
        _responses = responses;
        _localizer = localizer;
        _executor = executor;
        _notifier = notifier;
        _logger = logger;
    }

    public SpringPage<VideoResponse> GetVideoList(PageRequest page)
    {
        var result = _videoRepository.FindPaged(new Dictionary<string, string>(), page);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("recovered.video.page"));
        return SpringPageFactory.Create(Map(result, VideoResponse.FromEntity), page.Sorts);
    }

    public SpringPage<VideoRequest> GetAllVideoList(IReadOnlyDictionary<string, string> filters, PageRequest page)
    {
        var result = _videoRepository.FindPaged(filters, page);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("recovered.video"));
        return SpringPageFactory.Create(Map(result, WithRelations), page.Sorts);
    }

    /// <summary>The live page, one row per playlist: see <see cref="VideoRepository.FindLivePage"/>.</summary>
    public SpringPage<LiveRow> GetLivePage(LiveStatus? liveStatus, string? channelName, PageRequest page, string? platform = null)
    {
        var result = _videoRepository.FindLivePage(liveStatus, channelName, page.Page, page.Size, platform: platform);
        return SpringPageFactory.Create(
            Map(result, row => new LiveRow(WithRelations(row.Video), row.Position, row.Total, row.Status, SceneNameOf(row.Video))),
            page.Sorts);
    }

    /// <summary>The name a canvas live goes by: the scene it was started from, or a plain word for
    /// one deleted since (its rows keep every source, so the live still restarts). Null for a file.</summary>
    private string? SceneNameOf(VideoEntity video) => video.ScenePkid is not { } scenePkid
        ? null
        : _sceneRepository.FindByPkid(scenePkid)?.Name is { Length: > 0 } name ? name : "#" + scenePkid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every row of a live, in streaming order, for its details dialog.</summary>
    public PlaylistDetails? GetPlaylist(long videoLiveHistoryPkid)
    {
        var history = _videoLiveHistoryRepository.FindByPkid(videoLiveHistoryPkid);
        var rows = _videoRepository.FindByLiveHistoryId(videoLiveHistoryPkid);
        var videos = rows.Select(VideoRequest.FromEntity).ToList();

        if (history is null || videos.Count == 0)
        {
            return null;
        }

        // The same pick as the row of the page, asked of the same query, so the two never disagree.
        var current = _videoRepository.FindLivePage(null, null, 0, 1, videoLiveHistoryPkid).Items.FirstOrDefault();
        return new PlaylistDetails(
            VideoLiveHistoryRequest.FromEntity(history),
            videos,
            current?.Video.Pkid,
            current?.Status ?? LiveStatus.Offline,
            rows.Count > 0 ? SceneNameOf(rows[0]) : null);
    }

    /// <summary>The whole playlist leaves the page. Refused while one of its videos is on air, for
    /// the same reason as a single row: ffmpeg reads the row to know when to stop.</summary>
    public MessageResponse DeletePlaylist(long videoLiveHistoryPkid)
    {
        var videos = _videoRepository.FindByLiveHistoryId(videoLiveHistoryPkid);

        if (videos.Any(video => video.LiveStatus == LiveStatus.Live))
        {
            throw new LiveException("video.delete.live");
        }

        _videoRepository.DeleteByLiveHistoryId(videoLiveHistoryPkid);

        // The scene of a live is only there to restart it: gone with its last row, never a layout.
        foreach (var scenePkid in videos.Select(video => video.ScenePkid).OfType<long>().Distinct())
        {
            if (_sceneRepository.FindByPkid(scenePkid) is { IsLayout: false } && !_sceneRepository.IsOnAir(scenePkid))
            {
                _sceneRepository.Delete(scenePkid);
            }
        }

        _notifier.Raise();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("delete.successful"));
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    /// <summary>Deletes a single live history row and all its video rows. Refused if any video is live.</summary>
    public MessageResponse DeleteLiveHistory(long videoLiveHistoryPkid)
    {
        _cleanup.DeleteLive(videoLiveHistoryPkid);

        _logger.LogInformation("{Message}", _localizer.PrintMessage("delete.successful"));
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    /// <summary>
    /// Deletes all live history rows older than the given number of months and their video rows, on
    /// the calling thread: this is the answer of the API and of the scheduler, while the page queues
    /// the same work and watches it go (see <see cref="LiveHistoryCleanupService"/>).
    /// </summary>
    public MessageResponse DeleteOldLiveHistory(int monthsOld)
    {
        if (monthsOld < LiveHistoryCleanupService.Everything)
        {
            return _responses.Build("invalid.parameter", StatusCodes.Status400BadRequest);
        }

        var progress = _cleanup.Run(monthsOld);
        _logger.LogInformation(
            "Deleted {Videos} video rows of {Lives} live history rows older than {Months} months",
            progress.DeletedVideos, progress.DeletedLives, monthsOld);

        if (progress.Error is { } error)
        {
            return _responses.Build("cleanupError", StatusCodes.Status500InternalServerError, [error]);
        }

        // The envelope carries a message code, not a sentence: a sentence built here is looked up in
        // the bundles as if it were a code, and the lookup miss answers a 500 to a cleanup that did
        // its work.
        return _responses.Build("cleanupSuccess", StatusCodes.Status200OK, [progress.DeletedLives]);
    }

    /// <summary>One video with its live history and setting, the payload <c>/live/start-video-live</c> expects.</summary>
    public VideoRequest FindVideo(int pkid) => WithRelations(FindVideoToUnlock(pkid));

    /// <summary>Videos still marked LIVE, grouped by channel: the ones the unlock dialog can release.</summary>
    public List<ChannelResponse> GetLockedVideos() =>
        _videoRepository.FindByLiveStatus(LiveStatus.Live)
            .GroupBy(video => video.ChannelName, StringComparer.Ordinal)
            .Select(group => new ChannelResponse(
                group.Key,
                group.Select(video => new VideoPathAndKeyResponse(video.VideoPath, video.Pkid)).ToList()))
            .ToList();

    /// <summary>
    /// The JPA entity serialized its live history and video setting: the cards need them to
    /// show the platform, enable stop/restart, and replay a video (<c>/live/start-video-live</c>).
    /// </summary>
    private VideoRequest WithRelations(VideoEntity entity)
    {
        var history = entity.VideoLiveHistoryId is { } historyId ? _videoLiveHistoryRepository.FindByPkid(historyId) : null;
        var setting = entity.VideoSettingId is { } settingId ? _videoSettingRepository.FindById(settingId) : null;
        return VideoRequest.FromEntity(entity) with
        {
            VideoLiveHistory = history is null ? null : VideoLiveHistoryRequest.FromEntity(history),
            VideoSetting = setting is null ? null : VideoSettingsRequest.FromEntity(setting)
        };
    }

    private static PagedResult<TTarget> Map<TSource, TTarget>(
        PagedResult<TSource> source,
        Func<TSource, TTarget> projection) => new(
        source.Items.Select(projection).ToList(),
        source.Page,
        source.Size,
        source.TotalElements);

    /// <summary>All channels with the live videos currently streaming, as the stop card needs.</summary>
    public List<ChannelResponse> GetAllChannelOnline()
    {
        var videoList = _videoRepository.FindByLiveStatus(LiveStatus.Live);
        if (videoList.Count == 0)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("nothing.to.stop"));
            throw new NotFoundCustomException("nothing.to.stop");
        }

        _logger.LogInformation("found video to stop");

        return videoList
            .Select(video => video.ChannelName)
            .Distinct()
            .Select(channelName => new ChannelResponse(
                channelName,
                videoList
                    .Where(video => string.Equals(video.ChannelName, channelName, StringComparison.Ordinal))
                    .Select(video => new VideoPathAndKeyResponse(video.VideoPath, video.Pkid))
                    .ToList()))
            .ToList();
    }

    /// <summary>Port of <c>unlockVideo</c>: asks the stream to stop and forces it offline after 3 checks.</summary>
    public MessageResponse UnlockVideo(int videoKey)
    {
        var video = FindVideoToUnlock(videoKey);
        _notifier.Raise();
        _executor.Execute(() => _ = UnlockAsync(video));

        return _responses.Build("unlock.in.progress", StatusCodes.Status202Accepted);
    }

    /// <summary>Removes one row of the live list. A row still LIVE has an ffmpeg behind it that
    /// reads this row to know when to stop, so it has to be stopped first.</summary>
    public MessageResponse DeleteVideo(int pkid)
    {
        var video = FindVideoToUnlock(pkid);
        if (video.LiveStatus == LiveStatus.Live)
        {
            throw new LiveException("video.delete.live");
        }

        _videoRepository.Delete(pkid);
        _notifier.Raise();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("delete.successful"));
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    private async Task UnlockAsync(VideoEntity video)
    {
        video.ShouldBeStop = true;
        _videoRepository.Update(video);
        _notifier.Raise();

        if (await CheckThreeTimesAsync(video.Pkid).ConfigureAwait(false))
        {
            return;
        }

        video.LiveStatus = LiveStatus.Offline;
        _videoRepository.Update(video);
    }

    private async Task<bool> CheckThreeTimesAsync(int videoPkid)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CheckIfVideoGoOffline(videoPkid))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        return false;
    }

    private bool CheckIfVideoGoOffline(int videoPkid)
    {
        var video = _videoRepository.FindByPkid(videoPkid);
        if (video is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("video.not.found"));
            throw new NotFoundCustomException("video.not.found");
        }

        return video.LiveStatus == LiveStatus.Offline;
    }

    private VideoEntity FindVideoToUnlock(int videoKey)
    {
        var video = _videoRepository.FindByPkid(videoKey);
        if (video is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("video.not.found"));
            throw new NotFoundCustomException("video.not.found");
        }

        return video;
    }
}
