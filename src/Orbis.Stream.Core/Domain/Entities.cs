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
}
