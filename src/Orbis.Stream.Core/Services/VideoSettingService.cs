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

    public MessageResponse LinkAndSaveSettingsVideo(VideoSettingsRequest request, int videoPkidToLink)
    {
        ArgumentNullException.ThrowIfNull(request);

        var video = FindVideoByPkid(videoPkidToLink);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("video.to.link.found"));

        var setting = request.ToEntity();
        setting.LastModified = DateTime.Now;
        var settingId = _videoSettingRepository.Insert(setting);

        video.VideoSettingId = settingId;
        _videoRepository.Update(video);
        _notifier.Raise();

        _logger.LogInformation("{Message}", _localizer.PrintMessage("video.settings.saved"));
        _logger.LogInformation("{Message}", _localizer.PrintMessage("success.operations"));

        return _responses.Build("success.operations", StatusCodes.Status201Created);
    }

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
