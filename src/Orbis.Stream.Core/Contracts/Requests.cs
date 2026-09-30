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
    string? ChannelName);

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
    string? ChannelName)
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
        entity.ChannelName);
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

/// <summary>A canvas, with the sources on it in stacking order.</summary>
public sealed record SceneRequest(
    long? Pkid,
    string? Name,
    string? Description,
    int? Width,
    int? Height,
    IReadOnlyList<SceneItemRequest>? Items)
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
            item.AudioEnabled))]);

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
            Items = [.. (Items ?? []).Select(item => item.ToEntity(scenePkid))]
        };
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

/// <summary>A row of the live page. <see cref="Total"/> above one means the row stands for a whole
/// folder playlist, <see cref="Video"/> is the video it got to and <see cref="Status"/> the status
/// of the playlist as a whole.</summary>
public sealed record LiveRow(VideoRequest Video, int Position, int Total, LiveStatus Status)
{
    public bool IsPlaylist => Total > 1;
}

/// <summary>A playlist opened in its details dialog: where it streams from, all its videos in order,
/// and the one it got to (the one its row on the page shows).</summary>
public sealed record PlaylistDetails(
    VideoLiveHistoryRequest History, IReadOnlyList<VideoRequest> Videos, int? CurrentPkid, LiveStatus Status);
