using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Contracts;

/// <summary>Port of <c>com.orbis.stream.record.SettingRecord</c> (request body of <c>/settings/save</c> and <c>/settings/change</c>).</summary>
public sealed record SettingRequest(
    string? StreamUrl,
    string? StreamKey,
    string? PlatformStreamName,
    string? Description,
    string? VideoFolder,
    bool? IsActive,
    string? ChannelName,
    bool AutoCleanupEnabled,
    int AutoCleanupIntervalMonths,
    int AutoCleanupOlderThanMonths,
    // Null leaves the stored value alone, the way every other field of an edit does.
    bool? FfmpegSender = null);

/// <summary>Port of <c>com.orbis.stream.dto.SettingDto</c>.</summary>
public sealed record SettingResponse(
    int? Id,
    string? StreamUrl,
    string? StreamKey,
    string? PlatformStreamName,
    string? Description,
    string? VideoFolder,
    int? GopSize,
    DateTime? LastModified,
    bool? IsActive,
    string? ChannelName,
    bool AutoCleanupEnabled,
    int AutoCleanupIntervalMonths,
    int AutoCleanupOlderThanMonths,
    bool FfmpegSender = false)
{
    public static SettingResponse FromEntity(SettingEntity entity) => new(
        entity.Id,
        entity.StreamUrl,
        entity.StreamKey,
        entity.PlatformStreamName,
        entity.Description,
        entity.VideoFolder,
        null,
        null,
        entity.IsActive,
        entity.ChannelName,
        entity.AutoCleanupEnabled,
        entity.AutoCleanupIntervalMonths,
        entity.AutoCleanupOlderThanMonths,
        entity.FfmpegSender);
}

/// <summary>Port of <c>com.orbis.stream.record.output.VideoPathRecord</c>.</summary>
public sealed record VideoPathResponse(string? Path, bool IsAFolder);

/// <summary>Port of <c>com.orbis.stream.record.output.VideoPathAndKey</c>.</summary>
public sealed record VideoPathAndKeyResponse(string? VideoPath, int? VideoKey);

/// <summary>Port of <c>com.orbis.stream.record.output.ChannelRecord</c>.</summary>
public sealed record ChannelResponse(string? ChannelName, List<VideoPathAndKeyResponse> VideoPathAndKeyList);

/// <summary>Port of <c>com.orbis.stream.record.AudioSettingsRecord</c>.</summary>
public sealed record AudioSettingsRequest(int? AudioCodec, int? AudioBitrate)
{
    public static AudioSettingsRequest? FromEntity(AudioSettingEntity? entity) => entity is null
        ? null
        : new AudioSettingsRequest(entity.AudioCodec, entity.AudioBitrate);
}

/// <summary>Port of <c>com.orbis.stream.record.VideoOptionRecord</c>.</summary>
public sealed record VideoOptionRequest(string? Key, string? Value)
{
    public static VideoOptionRequest? FromEntity(VideoSettingsOptionEntity? entity) => entity is null
        ? null
        : new VideoOptionRequest(entity.Key, entity.Value);
}

/// <summary>Port of <c>com.orbis.stream.record.VideoSettingsRecord</c>.</summary>
public sealed record VideoSettingsRequest(
    int? Id,
    string? Title,
    int? VideoCodec,
    string? VideoCodecName,
    int? PixelFormat,
    int? VideoBitrate,
    DateTime? LastModified,
    bool? IsVideoAndAudioSettingActive,
    int? GopSize,
    VideoOptionRequest[]? VideoOptions,
    string? VideoFormat,
    AudioSettingsRequest? AudioSettingRecord,
    bool? IsDefaultConfiguration,
    string? DefaultPlatformConfiguration,
    int? VideoWidth,
    int? VideoHeight,
    double? FrameRate)
{
    public static VideoSettingsRequest FromEntity(VideoSettingEntity entity) => new(
        entity.Id,
        entity.Title,
        entity.VideoCodec,
        entity.VideoCodecName,
        entity.PixelFormat,
        entity.VideoBitrate,
        entity.LastModified,
        entity.IsVideoAndAudioSettingActive,
        entity.GopSize,
        entity.VideoSettingsOptions.Select(option => VideoOptionRequest.FromEntity(option)!).ToArray(),
        entity.VideoFormat,
        AudioSettingsRequest.FromEntity(entity.AudioSetting),
        entity.IsDefaultConfiguration,
        entity.DefaultPlatformConfiguration,
        entity.VideoWidth,
        entity.VideoHeight,
        entity.FrameRate);

    public VideoSettingEntity ToEntity() => new()
    {
        Id = Id,
        Title = Title,
        VideoCodec = VideoCodec,
        VideoCodecName = VideoCodecName,
        PixelFormat = PixelFormat,
        VideoBitrate = VideoBitrate,
        VideoFormat = VideoFormat,
        LastModified = LastModified,
        IsVideoAndAudioSettingActive = IsVideoAndAudioSettingActive,
        GopSize = GopSize,
        VideoWidth = VideoWidth,
        VideoHeight = VideoHeight,
        FrameRate = FrameRate,
        IsDefaultConfiguration = IsDefaultConfiguration,
        DefaultPlatformConfiguration = DefaultPlatformConfiguration,
        AudioSetting = AudioSettingRecord is null
            ? null
            : new AudioSettingEntity
            {
                AudioCodec = AudioSettingRecord.AudioCodec,
                AudioBitrate = AudioSettingRecord.AudioBitrate
            },
        VideoSettingsOptions = VideoOptions?
            .Where(option => option is not null)
            .Select(option => new VideoSettingsOptionEntity { Key = option!.Key, Value = option.Value })
            .ToList() ?? []
    };
}

/// <summary>
/// The parameters the preview page can change while a live is running. Every field is optional:
/// one that travels null is left as it is, so the page can send the single control that changed
/// without having to know the whole configuration. Zero is a value and not an absence: it is how
/// <see cref="VideoWidth"/>, <see cref="VideoHeight"/> and <see cref="FrameRate"/> ask for the source
/// again, which the page could not do with a field it simply left out.
/// </summary>
public sealed record LiveParameterRequest(
    int? VideoCodec,
    string? VideoCodecName,
    int? PixelFormat,
    int? VideoBitrate,
    int? AudioBitrate,
    int? VideoWidth,
    int? VideoHeight,
    double? FrameRate);

/// <summary>How loud one source of a canvas is in the mix of a running live, in percent.</summary>
public sealed record LiveVolumeRequest(int SourcePkid, int Volume);

/// <summary>
/// A button of the scene deck as the panel saves it: its name, the file it puts on air (a name of
/// the scene media folder, uploaded beforehand) and the key that asks for it, if any.
/// </summary>
public sealed record SceneButtonRequest(
    string? Label,
    string? MediaName,
    string? Hotkey,
    string? DisplayMode = "fullscreen",
    string? Placement = "bottom-right",
    int? DurationSeconds = null,
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null);

/// <summary>Port of <c>com.orbis.stream.record.VideoLiveHistoryRecord</c>.</summary>
public sealed record VideoLiveHistoryRequest(
    long? Pkid,
    string? FolderOfVideoToStream,
    DateTime? LocalDateTimeStartLive,
    string? StreamUrl,
    string? StreamKey,
    string? PlatformStreamName,
    string? UserName)
{
    public static VideoLiveHistoryRequest FromEntity(VideoLiveHistoryEntity entity) => new(
        entity.Pkid,
        entity.FolderOfVideoToStream,
        entity.LocalDateTimeStartLive,
        entity.StreamUrl,
        entity.StreamKey,
        entity.PlatformStreamName,
        entity.UserName);

    public VideoLiveHistoryEntity ToEntity() => new()
    {
        Pkid = Pkid ?? 0,
        FolderOfVideoToStream = FolderOfVideoToStream ?? string.Empty,
        LocalDateTimeStartLive = LocalDateTimeStartLive ?? default,
        StreamUrl = StreamUrl ?? "N/A",
        StreamKey = StreamKey ?? "N/A",
        PlatformStreamName = PlatformStreamName ?? "N/A",
        UserName = UserName
    };
}

/// <summary>Port of <c>com.orbis.stream.dto.VideoDto</c>.</summary>
public sealed record VideoResponse(
    int? Pkid,
    string? Name,
    string? VideoPath,
    string? Extension,
    LiveStatus? LiveStatus,
    long? LastTimeStampBeforeStop,
    string? Message,
    bool? ShouldBeStop,
    DateTime? StartDateLive,
    string? ChannelName)
{
    public static VideoResponse FromEntity(VideoEntity entity) => new(
        entity.Pkid,
        entity.Name,
        entity.VideoPath,
        entity.Extension,
        entity.LiveStatus,
        entity.LastTimeStampBeforeStop,
        entity.Message,
        entity.ShouldBeStop,
        entity.StartDateLive,
        entity.ChannelName);
}

/// <summary>Port of <c>com.orbis.stream.record.VideoRecord</c> (request body and response of the video listing).</summary>
public sealed record VideoRequest(
    int? Pkid,
    string? Name,
    string? VideoPath,
    string? Extension,
    LiveStatus? LiveStatus,
    long? LastTimeStampBeforeStop,
    string? Message,
    VideoLiveHistoryRequest? VideoLiveHistory,
    VideoSettingsRequest? VideoSetting,
    bool? ShouldBeStop,
    DateTime? StartDateLive,
    string? ChannelName)
{
    public static VideoRequest FromEntity(VideoEntity entity) => new(
        entity.Pkid,
        entity.Name,
        entity.VideoPath,
        entity.Extension,
        entity.LiveStatus,
        entity.LastTimeStampBeforeStop,
        entity.Message,
        null,
        null,
        entity.ShouldBeStop,
        entity.StartDateLive,
        entity.ChannelName);
}

/// <summary>Port of <c>com.orbis.stream.record.StartLiveRecord</c>.</summary>
public sealed record StartLiveRequest(
    string? StreamUrl,
    string? StreamKey,
    string? VideoPath,
    string? PlatformStreamName,
    string? ChannelName,
    VideoSettingsRequest? VideoSettingsRecord);

/// <summary>
/// Starts a live from a saved canvas instead of a folder. There is no path on purpose: the sources
/// are the rows of the scene, and the encoder setting is the same one a folder start takes.
/// </summary>
public sealed record StartSceneLiveRequest(
    long ScenePkid,
    string? StreamUrl,
    string? StreamKey,
    string? PlatformStreamName,
    string? ChannelName,
    VideoSettingsRequest? VideoSettingsRecord);

/// <summary>One source on a canvas, as the page sends it.</summary>
public sealed record SceneItemRequest(
    SourceKind SourceKind,
    string SourceTarget,
    string? Label,
    int X,
    int Y,
    int Width,
    int Height,
    bool AudioEnabled)
{
    public SceneItemEntity ToEntity(long scenePkid) => new()
    {
        ScenePkid = scenePkid,
        SourceKind = SourceKind,
        SourceTarget = SourceTarget,
        Label = Label,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        AudioEnabled = AudioEnabled
    };
}

/// <summary>
/// A canvas, with the sources on it in stacking order. A layout (<see cref="IsLayout"/>) carries
/// slots instead: the rectangles are kept and whatever source came with them is dropped. Its
/// overlays are the exception: they are what the layout is dressed with, so they are kept whole.
/// </summary>
public sealed record SceneRequest(
    long? Pkid,
    string? Name,
    string? Description,
    int? Width,
    int? Height,
    IReadOnlyList<SceneItemRequest>? Items,
    bool IsLayout = false)
{
    public static SceneRequest FromEntity(SceneEntity scene) => new(
        scene.Pkid,
        scene.Name,
        scene.Description,
        scene.Width,
        scene.Height,
        [.. scene.Items.Select(item => new SceneItemRequest(
            item.SourceKind,
            item.SourceTarget,
            item.Label,
            item.X,
            item.Y,
            item.Width,
            item.Height,
            item.AudioEnabled))],
        scene.IsLayout);

    /// <summary>
    /// The items carry the id the scene has, or 0 on a first save: the repository writes them
    /// with the id it has just given the scene, so the caller never needs to know it in advance.
    /// </summary>
    public SceneEntity ToEntity()
    {
        var scenePkid = Pkid ?? 0;
        return new SceneEntity
        {
            Pkid = scenePkid,
            Name = Name ?? string.Empty,
            Description = Description,
            Width = Width,
            Height = Height,
            LastModified = DateTime.Now,
            IsLayout = IsLayout,
            Items = [.. (Items ?? []).Select(item => item.SourceKind.IsOverlay()
                ? AsOverlay(item.ToEntity(scenePkid))
                : IsLayout ? AsSlot(item.ToEntity(scenePkid)) : item.ToEntity(scenePkid))]
        };
    }

    /// <summary>An overlay is a picture of the layout: it is laid over the canvas and never heard.</summary>
    private static SceneItemEntity AsOverlay(SceneItemEntity item)
    {
        item.AudioEnabled = false;
        return item;
    }

    /// <summary>A slot is a rectangle and nothing else: no source to open, nothing to hear.</summary>
    private static SceneItemEntity AsSlot(SceneItemEntity item)
    {
        item.SourceKind = SourceKind.File;
        item.SourceTarget = string.Empty;
        item.AudioEnabled = false;
        return item;
    }
}

/// <summary>
/// The answer to a save: the usual envelope, plus the id the canvas has now, which the page needs
/// to start a live from a scene it has only just created.
/// </summary>
public sealed record SceneSavedResponse(string Response, string Message, long Pkid);

/// <summary>Port of <c>com.orbis.stream.dto.SystemInfoDto</c>.</summary>
public sealed record SystemInfoResponse(string? Field, int Value);

/// <summary>Port of <c>com.orbis.stream.handler.ResponseHandler</c> payload: <c>{response, message}</c>.</summary>
public sealed record ApiEnvelope(string Response, string Message)
{
    public const string Success = "success";
    public const string Error = "error";
}

/// <summary>A row of the live page: one per live. <see cref="Video"/> is the video a playlist got
/// to, or the base source of a canvas (<see cref="SceneName"/> set); <see cref="Status"/> is the
/// status of the live as a whole, and <see cref="Total"/> how many rows it stands for.</summary>
public sealed record LiveRow(VideoRequest Video, int Position, int Total, LiveStatus Status, string? SceneName = null)
{
    public bool IsScene => SceneName is not null;

    public bool IsPlaylist => !IsScene && Total > 1;

    /// <summary>The rows behind it are only reachable from its details dialog.</summary>
    public bool HasDetails => Total > 1 || IsScene;
}

/// <summary>A live opened in its details dialog: where it streams from, all its rows in order (the
/// videos of a playlist, the sources of a canvas) and the one its row on the page shows.</summary>
public sealed record PlaylistDetails(
    VideoLiveHistoryRequest History,
    IReadOnlyList<VideoRequest> Videos,
    int? CurrentPkid,
    LiveStatus Status,
    string? SceneName = null)
{
    public bool IsScene => SceneName is not null;
}
