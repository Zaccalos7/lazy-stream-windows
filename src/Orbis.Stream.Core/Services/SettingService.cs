using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.service.SettingService</c>.</summary>
public sealed class SettingService
{
    private readonly SettingRepository _settingRepository;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly ILogger<SettingService> _logger;

    public SettingService(
        SettingRepository settingRepository,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<SettingService> logger)
    {
        _settingRepository = settingRepository;
        _responses = responses;
        _localizer = localizer;
        _logger = logger;
    }

    public MessageResponse AddNewConfiguration(SettingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CheckUniqueConstraint(request.StreamKey!.Trim(), request.StreamUrl!.Trim());
        _settingRepository.Insert(ToEntity(request));

        return _responses.Build("setting.created", StatusCodes.Status201Created);
    }

    public MessageResponse ModifySetting(int id, SettingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setting = _settingRepository.FindById(id);
        if (setting is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("setting.not.found"));
            throw new NotFoundCustomException("setting.not.found");
        }

        ApplyNonNullValues(request, setting);
        _settingRepository.Update(setting);

        return _responses.Build("setting.update", StatusCodes.Status202Accepted);
    }

    public MessageResponse DeleteAStreamingSetting(int id)
    {
        var setting = _settingRepository.FindById(id);
        if (setting is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("setting.not.found"));
            throw new NotFoundCustomException("setting.not.found");
        }

        _settingRepository.Delete(id);
        return _responses.Build("setting.delete", StatusCodes.Status200OK);
    }

    public List<SettingResponse> RetrieveSettings(IReadOnlyDictionary<string, string> filters) =>
        RetrieveSettingsWithFiltersOrNot(filters).Select(SettingResponse.FromEntity).ToList();

    /// <summary>
    /// Saves the auto cleanup settings on the active configuration. They live on the configuration
    /// row rather than on the live history page, so they cannot go through <see cref="ModifySetting"/>:
    /// that one carries the whole record, and a form that only shows the three cleanup fields would
    /// write an empty stream url over a working configuration.
    /// </summary>
    public MessageResponse SaveAutoCleanup(bool enabled, int intervalMonths, int olderThanMonths)
    {
        if (intervalMonths is < 0 or > 12 || olderThanMonths < LiveHistoryCleanupService.Everything)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["autoCleanupIntervalMonths"] = _localizer.PrintMessage("not.valid.input")
            });
        }

        var active = RetrieveSettingsWithFiltersOrNot(new Dictionary<string, string>())
            .FirstOrDefault(setting => setting.IsActive == true);
        if (active is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("setting.not.found"));
            throw new NotFoundCustomException("setting.not.found");
        }

        // The picks are kept while the cleanup is off, so turning it back on finds the same period
        // where it was left rather than the first one of the list.
        active.AutoCleanupEnabled = enabled;
        active.AutoCleanupIntervalMonths = enabled ? intervalMonths : active.AutoCleanupIntervalMonths;
        active.AutoCleanupOlderThanMonths = enabled ? olderThanMonths : active.AutoCleanupOlderThanMonths;
        _settingRepository.Update(active);

        return _responses.Build("setting.update", StatusCodes.Status202Accepted);
    }

    public HashSet<string> RetrieveChannel(IReadOnlyDictionary<string, string> filters) =>
        RetrieveSettingsWithFiltersOrNot(filters)
            .Select(setting => setting.ChannelName)
            .ToHashSet(StringComparer.Ordinal);

    public List<VideoPathResponse> RetrieveDirectoriesSettingsPath(IReadOnlyDictionary<string, string> filters)
    {
        var result = RetrieveSettingsWithFiltersOrNot(filters)
            .Select(setting => new VideoPathResponse(setting.VideoFolder, Directory.Exists(setting.VideoFolder)))
            .ToList();

        _logger.LogTrace("{Message}", _localizer.PrintMessage("recovered.directories.path"));
        return result;
    }

    private List<SettingEntity> RetrieveSettingsWithFiltersOrNot(IReadOnlyDictionary<string, string> filters) =>
        _settingRepository.FindAll(filters);

    /// <summary>
    /// The MapStruct mapper used <c>NullValuePropertyMappingStrategy.IGNORE</c>, so a field that
    /// is absent from the payload keeps the stored value.
    /// </summary>
    private static void ApplyNonNullValues(SettingRequest request, SettingEntity setting)
    {
        setting.StreamUrl = request.StreamUrl?.Trim() ?? setting.StreamUrl;
        setting.StreamKey = request.StreamKey?.Trim() ?? setting.StreamKey;
        setting.PlatformStreamName = request.PlatformStreamName ?? setting.PlatformStreamName;
        setting.Description = request.Description ?? setting.Description;
        setting.VideoFolder = request.VideoFolder ?? setting.VideoFolder;
        setting.IsActive = request.IsActive ?? setting.IsActive;
        setting.ChannelName = request.ChannelName ?? setting.ChannelName;
    }

    private static SettingEntity ToEntity(SettingRequest request) => new()
    {
        // Pasted from the dashboard of a platform: see FfmpegCommandBuilder.BuildStreamingUrl.
        StreamUrl = request.StreamUrl!.Trim(),
        StreamKey = request.StreamKey!.Trim(),
        PlatformStreamName = request.PlatformStreamName,
        Description = request.Description,
        // The column is NOT NULL for the rows written before the folder left the form.
        VideoFolder = request.VideoFolder ?? string.Empty,
        IsActive = request.IsActive,
        ChannelName = request.ChannelName!
    };

    private void CheckUniqueConstraint(string streamKey, string streamUrl)
    {
        if (_settingRepository.FindByStreamUrlAndStreamKey(streamUrl, streamKey) is not null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("setting.is.already.present"));
            throw new DuplicationEntityException("setting.is.already.present");
        }
    }
}
