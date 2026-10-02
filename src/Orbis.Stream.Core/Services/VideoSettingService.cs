using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.service.VideoSettingService</c>.</summary>
public sealed class VideoSettingService
{
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly VideoRepository _videoRepository;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<VideoSettingService> _logger;

    public VideoSettingService(
        VideoSettingRepository videoSettingRepository,
        VideoRepository videoRepository,
        ResponseFactory responses,
        Localizer localizer,
        LiveChangeNotifier notifier,
        ILogger<VideoSettingService> logger)
    {
        _videoSettingRepository = videoSettingRepository;
        _videoRepository = videoRepository;
        _responses = responses;
        _localizer = localizer;
        _notifier = notifier;
        _logger = logger;
    }

    public MessageResponse SaveSettingsVideo(VideoSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        MappingAndSaveValue(request);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("video.settings.saved"));

        return _responses.Build("video.settings.saved", StatusCodes.Status201Created);
    }

    /// <summary>
    /// The setting of a row of the live page, for the link dialog to open on: the one the row
    /// streams with, not a blank form. Null when the row has none yet.
    /// </summary>
    public VideoSettingsRequest? FindSettingOf(int? videoPkid, long? videoLiveHistoryPkid)
    {
        var settingId = RowsToLink(videoPkid, videoLiveHistoryPkid).Select(row => row.VideoSettingId).FirstOrDefault(id => id is not null);
        return settingId is { } id && _videoSettingRepository.FindById(id) is { } setting
            ? VideoSettingsRequest.FromEntity(setting)
            : null;
    }

    public MessageResponse LinkAndSaveSettingsVideo(VideoSettingsRequest request, int videoPkidToLink)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = RowsToLink(videoPkidToLink, null);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("video.to.link.found"));
        return LinkSetting(request, rows);
    }

    public MessageResponse LinkAndSaveSettingsPlaylist(VideoSettingsRequest request, long videoLiveHistoryPkid)
    {
        ArgumentNullException.ThrowIfNull(request);
        return LinkSetting(request, RowsToLink(null, videoLiveHistoryPkid));
    }

    /// <summary>
    /// The rows one link reaches: a playlist as a whole, and a video on its own, unless it is a
    /// source of a canvas, whose rows are one live and stream with one setting.
    /// </summary>
    private IReadOnlyList<VideoEntity> RowsToLink(int? videoPkid, long? videoLiveHistoryPkid)
    {
        if (videoLiveHistoryPkid is { } history)
        {
            var playlist = _videoRepository.FindByLiveHistoryId(history);
            return playlist.Count > 0 ? playlist : throw new NotFoundCustomException("video.to.link.not.found");
        }

        var video = FindVideoByPkid(videoPkid ?? 0);
        return video.ScenePkid is not null && video.VideoLiveHistoryId is { } canvas
            ? _videoRepository.FindByLiveHistoryId(canvas)
            : [video];
    }

    /// <summary>
    /// The setting of the rows is edited where it is, so the row keeps its own setting instead of
    /// collecting a new one per save. Unless it is not only theirs: a seeded default, a setting the
    /// live wizard offers, or one another live streams with would change under the feet of
    /// everything else that uses it, so those rows get a copy of their own instead.
    /// </summary>
    private MessageResponse LinkSetting(VideoSettingsRequest request, IReadOnlyList<VideoEntity> rows)
    {
        var setting = request.ToEntity();
        setting.LastModified = DateTime.Now;

        var current = rows.Select(row => row.VideoSettingId).OfType<int>().Distinct().ToList();
        if (current.Count == 1 && IsOnlyOf(current[0], rows))
        {
            setting.Id = current[0];
            _videoSettingRepository.Update(setting);
            _logger.LogInformation("{Message}", _localizer.PrintMessage("video.settings.modified"));
        }
        else
        {
            setting.Id = null;
            // A copy for one live is neither a default nor a choice of the wizard: it stays with the
            // rows it was made for.
            setting.IsDefaultConfiguration = false;
            setting.IsVideoAndAudioSettingActive = false;
            setting.Id = _videoSettingRepository.Insert(setting);
            _logger.LogInformation("{Message}", _localizer.PrintMessage("video.settings.saved"));
        }

        foreach (var row in rows.Where(row => row.VideoSettingId != setting.Id))
        {
            _videoRepository.SetVideoSetting(row.Pkid, setting.Id);
        }

        _notifier.Raise();
        _logger.LogInformation("{Message}", _localizer.PrintMessage("success.operations"));
        return _responses.Build("success.operations", StatusCodes.Status201Created);
    }

    private bool IsOnlyOf(int settingId, IReadOnlyList<VideoEntity> rows) =>
        _videoSettingRepository.FindById(settingId) is { IsDefaultConfiguration: not true, IsVideoAndAudioSettingActive: not true }
        && _videoRepository.FindPkidsByVideoSettingId(settingId).All(pkid => rows.Any(row => row.Pkid == pkid));

    public List<VideoSettingsRequest> GetAllVideoSettings(IReadOnlyDictionary<string, string> filters) =>
        _videoSettingRepository.FindAll(filters).Select(VideoSettingsRequest.FromEntity).ToList();

    public MessageResponse EditSettingsVideo(VideoSettingsRequest request, int id)
    {
        ArgumentNullException.ThrowIfNull(request);

        CheckIfVideoSettingExist(id);

        var setting = request.ToEntity();
        setting.Id = id;
        setting.LastModified = DateTime.Now;
        _videoSettingRepository.Update(setting);

        _logger.LogInformation("{Message}", _localizer.PrintMessage("video.settings.modified"));
        return _responses.Build("video.settings.modified", StatusCodes.Status201Created);
    }

    public MessageResponse DeleteVideoSetting(int id)
    {
        _videoSettingRepository.Delete(id);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("delete.successful"));
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    private void MappingAndSaveValue(VideoSettingsRequest request)
    {
        var setting = request.ToEntity();
        setting.LastModified = DateTime.Now;
        _videoSettingRepository.Insert(setting);
    }

    private void CheckIfVideoSettingExist(int id)
    {
        if (_videoSettingRepository.FindById(id) is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("video.settings.not.found"));
            throw new NotFoundCustomException("video.settings.not.found");
        }
    }

    private VideoEntity FindVideoByPkid(int videoPkidToLink)
    {
        var video = _videoRepository.FindByPkid(videoPkidToLink);
        if (video is null)
        {
            throw new NotFoundCustomException("video.to.link.not.found");
        }

        return video;
    }
}
