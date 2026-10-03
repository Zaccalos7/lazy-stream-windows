using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// How far the deletion of the old live history has got. It counts <em>videos</em> and not lives:
/// a live of a playlist of twenty files is twenty videos, and those are what the user watches go by.
/// </summary>
public sealed record LiveHistoryCleanupProgress(
    bool Running,
    int TotalVideos,
    int DeletedVideos,
    string CurrentVideo,
    int DeletedLives,
    string? Error)
{
    public static LiveHistoryCleanupProgress Idle { get; } = new(false, 0, 0, string.Empty, 0, null);
}

/// <summary>
/// Everything that takes a live off the database: the row of the history, the video rows under it
/// and the scene it was streamed from.
/// <para>Deleting is a walk over the videos, one at a time, because the page shows the walk: a
/// cleanup of a hundred rows answers in a flash and leaves the user looking at a page that did not
/// move. The progress lives here rather than in the page, because the request that starts the work,
/// the one that asks how far it has got and the one the scheduler makes are three different
/// requests, and they all have to read the same state.</para>
/// </summary>
public sealed class LiveHistoryCleanupService
{
    private readonly VideoRepository _videoRepository;
    private readonly VideoLiveHistoryRepository _videoLiveHistoryRepository;
    private readonly SceneRepository _sceneRepository;
    private readonly LiveChangeNotifier _notifier;
    private readonly BackgroundTaskExecutor _executor;
    private readonly ILogger<LiveHistoryCleanupService> _logger;
    private readonly Lock _gate = new();

    private LiveHistoryCleanupProgress _progress = LiveHistoryCleanupProgress.Idle;

    public LiveHistoryCleanupService(
        VideoRepository videoRepository,
        VideoLiveHistoryRepository videoLiveHistoryRepository,
        SceneRepository sceneRepository,
        LiveChangeNotifier notifier,
        BackgroundTaskExecutor executor,
        ILogger<LiveHistoryCleanupService> logger)
    {
        _videoRepository = videoRepository;
        _videoLiveHistoryRepository = videoLiveHistoryRepository;
        _sceneRepository = sceneRepository;
        _notifier = notifier;
        _executor = executor;
        _logger = logger;
    }

    public LiveHistoryCleanupProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>
    /// Queues the deletion of every live older than the period and answers at once, so the page can
    /// draw the walk while it goes on. False when a deletion is already running: two walks over the
    /// same rows would count the same video twice.
    /// </summary>
    public bool TryStart(int monthsOld)
    {
        lock (_gate)
        {
            if (_progress.Running)
            {
                return false;
            }

            _progress = LiveHistoryCleanupProgress.Idle with { Running = true };
        }

        try
        {
            _executor.Execute(() => Run(monthsOld));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The cleanup of the live history could not be queued");
            Publish(LiveHistoryCleanupProgress.Idle with { Error = exception.Message });
            throw;
        }

        return true;
    }

    /// <summary>Deletes every live older than the period, on the calling thread.</summary>
    public LiveHistoryCleanupProgress Run(int monthsOld)
    {
        var threshold = monthsOld == 0 ? DateTime.Now.AddDays(-1) : DateTime.Now.AddMonths(-monthsOld);
        var lives = DeletableLives(threshold);
        var videos = lives.SelectMany(life => _videoRepository.FindByLiveHistoryId(life.Pkid)).ToList();

        Publish(LiveHistoryCleanupProgress.Idle with { Running = true, TotalVideos = videos.Count });

        var deletedLives = 0;
        string? error = null;
        try
        {
            // The scenes are read before the first delete: a row that is gone cannot say which
            // canvas it belonged to.
            var scenes = videos.Select(video => video.ScenePkid).OfType<long>().Distinct().ToList();

            foreach (var video in videos)
            {
                Publish(Progress with { CurrentVideo = video.Name });
                _videoRepository.Delete(video.Pkid);
                Publish(Progress with { DeletedVideos = Progress.DeletedVideos + 1 });
                _notifier.Raise();
            }

            foreach (var life in lives)
            {
                _videoLiveHistoryRepository.DeleteRow(life.Pkid);
                deletedLives++;
            }

            DeleteScenes(scenes);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            _logger.LogError(exception, "The cleanup of the live history failed");
        }
        finally
        {
            _notifier.Raise();
            Publish(Progress with { Running = false, CurrentVideo = string.Empty, DeletedLives = deletedLives, Error = error });
        }

        return Progress;
    }

    /// <summary>
    /// Deletes one live with every video of it. Refused while one of its videos is on air: ffmpeg
    /// reads the row to know when to stop, and a row that is gone leaves it streaming to nowhere.
    /// </summary>
    public void DeleteLive(long historyPkid)
    {
        var videos = _videoRepository.FindByLiveHistoryId(historyPkid);
        if (videos.Any(video => video.LiveStatus == LiveStatus.Live))
        {
            throw new LiveException("video.delete.live");
        }

        var scenes = videos.Select(video => video.ScenePkid).OfType<long>().Distinct().ToList();

        foreach (var video in videos)
        {
            _videoRepository.Delete(video.Pkid);
        }

        _videoLiveHistoryRepository.DeleteRow(historyPkid);
        DeleteScenes(scenes);
        _notifier.Raise();
    }

    /// <summary>
    /// The lives the walk may touch. A live that is on air is left alone whatever its age: the files
    /// of a live being streamed are being read right now.
    /// </summary>
    private List<VideoLiveHistoryEntity> DeletableLives(DateTime threshold)
    {
        var lives = new List<VideoLiveHistoryEntity>();
        foreach (var life in _videoLiveHistoryRepository.FindOlderThan(threshold))
        {
            if (_videoRepository.FindByLiveHistoryId(life.Pkid).Any(video => video.LiveStatus == LiveStatus.Live))
            {
                _logger.LogInformation("Live history {Pkid} is on air and was left out of the cleanup", life.Pkid);
                continue;
            }

            lives.Add(life);
        }

        return lives;
    }

    /// <summary>
    /// The scene of a live is only there to restart it: gone with its last row, never a layout, and
    /// never while something is on air from it.
    /// </summary>
    private void DeleteScenes(IReadOnlyCollection<long> scenePkids)
    {
        foreach (var scenePkid in scenePkids)
        {
            if (_sceneRepository.FindByPkid(scenePkid) is { IsLayout: false } && !_sceneRepository.IsOnAir(scenePkid))
            {
                _sceneRepository.Delete(scenePkid);
            }
        }
    }

    private void Publish(LiveHistoryCleanupProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
        }
    }
}
