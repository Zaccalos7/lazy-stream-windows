namespace Orbis.Stream.Core.Domain;

/// <summary>Port of <c>com.orbis.stream.model.Setting</c>.</summary>
public sealed class SettingEntity
{
    public int Id { get; set; }

    public string StreamUrl { get; set; } = string.Empty;

    public string StreamKey { get; set; } = string.Empty;

    public string? PlatformStreamName { get; set; }

    public string? Description { get; set; }

    public string VideoFolder { get; set; } = "/";

    public bool? IsActive { get; set; }

    public string ChannelName { get; set; } = "Zingy";

    /// <summary>
    /// The canvas this channel streams, when it streams a composition instead of a folder of
    /// files. Null keeps the previous behaviour, so no existing configuration changes meaning.
    /// </summary>
    public long? ScenePkid { get; set; }
}

/// <summary>Port of <c>com.orbis.stream.model.VideoLiveHistory</c>.</summary>
public sealed class VideoLiveHistoryEntity
{
    public long Pkid { get; set; }

    public string FolderOfVideoToStream { get; set; } = string.Empty;

    public DateTime LocalDateTimeStartLive { get; set; }

    public string StreamUrl { get; set; } = "N/A";

    public string StreamKey { get; set; } = "N/A";

    public string PlatformStreamName { get; set; } = "N/A";

    public string? UserName { get; set; }
}

/// <summary>Port of <c>com.orbis.stream.model.AudioSetting</c>.</summary>
public sealed class AudioSettingEntity
{
    public int? Id { get; set; }

    public int? AudioCodec { get; set; }

    public int? AudioBitrate { get; set; }
}

/// <summary>Port of <c>com.orbis.stream.model.VideoSettingsOption</c> (embeddable key/value pair).</summary>
public sealed class VideoSettingsOptionEntity
{
    public string? Key { get; set; }

    public string? Value { get; set; }
}

/// <summary>Port of <c>com.orbis.stream.model.VideoSetting</c> with its owned audio setting.</summary>
public sealed class VideoSettingEntity
{
    public int? Id { get; set; }

    public string? Title { get; set; }

    public int? VideoCodec { get; set; }

    public string? VideoCodecName { get; set; }

    public int? PixelFormat { get; set; }

    public int? VideoBitrate { get; set; }

    public string? VideoFormat { get; set; }

    public DateTime? LastModified { get; set; }

    public bool? IsDefaultConfiguration { get; set; }

    public string? DefaultPlatformConfiguration { get; set; }

    public int? GopSize { get; set; }

    /// <summary>
    /// Width the encoder is asked to produce. Null (the two of them) means "keep the resolution
    /// of the file": the command builder only adds a <c>scale</c> filter when both are set, so a
    /// half filled pair is a mistake rather than a default.
    /// </summary>
    public int? VideoWidth { get; set; }

    /// <summary>Height the encoder is asked to produce; see <see cref="VideoWidth"/>.</summary>
    public int? VideoHeight { get; set; }

    /// <summary>
    /// Frames per second the encoder is asked to produce. Null keeps the rate ffprobe read from
    /// the file, which is what every stream did before this field existed.
    /// </summary>
    public double? FrameRate { get; set; }

    public bool? IsVideoAndAudioSettingActive { get; set; }

    public AudioSettingEntity? AudioSetting { get; set; }

    /// <summary>Identifier of the owned audio setting row (join column <c>audio_setting_id</c>).</summary>
    public int? AudioSettingId { get; set; }

    public List<VideoSettingsOptionEntity> VideoSettingsOptions { get; set; } = [];
}

/// <summary>Port of <c>com.orbis.stream.model.Video</c>.</summary>
public sealed class VideoEntity
{
    public int Pkid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string VideoPath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public LiveStatus LiveStatus { get; set; } = LiveStatus.Offline;

    public long LastTimeStampBeforeStop { get; set; }

    public string? Message { get; set; }

    public bool ShouldBeStop { get; set; }

    public DateTime? StartDateLive { get; set; }

    public string ChannelName { get; set; } = "Zingy";

    public long? VideoLiveHistoryId { get; set; }

    public int? VideoSettingId { get; set; }

    /// <summary>
    /// What this row is capturing. <see cref="SourceKind.File"/> is a file to play; the other two
    /// are capture devices, and they have no <see cref="VideoPath"/> in the usual sense: the device
    /// is named by <see cref="SourceTarget"/>.
    /// </summary>
    public SourceKind SourceKind { get; set; } = SourceKind.File;

    /// <summary>The device to capture: a gdigrab target or a dshow name. Null for a file.</summary>
    public string? SourceTarget { get; set; }

    /// <summary>Where the row came from on a canvas, when the live streams a composition.</summary>
    public long? ScenePkid { get; set; }

    /// <summary>Tile rectangle on the composed output, in output pixels. Null fills the canvas.</summary>
    public int? X { get; set; }

    public int? Y { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Whether this source contributes audio to the mix.</summary>
    public bool AudioEnabled { get; set; }
}

/// <summary>
/// A saved layout of sources: the blank canvas the user fills before starting a live. The rows of
/// <see cref="SceneItemEntity"/> are in the order they are stacked, so the first one is the base
/// the others are laid over.
/// </summary>
public sealed class SceneEntity
{
    public long Pkid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Width of the composed output. Null means "follow the base source".</summary>
    public int? Width { get; set; }

    /// <summary>Height of the composed output. Null means "follow the base source".</summary>
    public int? Height { get; set; }

    public DateTime? LastModified { get; set; }

    public List<SceneItemEntity> Items { get; set; } = [];
}

/// <summary>One source on a <see cref="SceneEntity"/>, and where it sits on it.</summary>
public sealed class SceneItemEntity
{
    public long Pkid { get; set; }

    public long ScenePkid { get; set; }

    public SourceKind SourceKind { get; set; } = SourceKind.File;

    /// <summary>A file path, a gdigrab target or a dshow device name, depending on the kind.</summary>
    public string SourceTarget { get; set; } = string.Empty;

    /// <summary>Shown on the tile, so a webcam does not have to be recognised by its device name.</summary>
    public string? Label { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool AudioEnabled { get; set; }
}
