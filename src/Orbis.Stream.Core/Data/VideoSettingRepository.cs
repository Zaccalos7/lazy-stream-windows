using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Data;

/// <summary>Port of <c>com.orbis.stream.repository.VideoSettingRepository</c> including the
/// <c>@ElementCollection</c> options table and the owned audio setting.</summary>
public sealed class VideoSettingRepository
{
    private const string BaseColumns =
        "t.id, t.title, t.video_codec, t.video_codec_name, t.pixel_format, t.video_bitrate, t.video_format, " +
        "t.last_modified, t.is_default_configuration, t.default_platform_configuration, t.gop_size, " +
        "t.is_video_and_audio_setting_active, t.audio_setting_id, t.video_width, t.video_height, t.frame_rate";

    private readonly SqliteConnectionFactory _connectionFactory;

    public VideoSettingRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public List<VideoSettingEntity> FindAll(IReadOnlyDictionary<string, string> filters)
    {
        using var connection = _connectionFactory.Open();

        var builder = new SqlQueryBuilder { TableAlias = "t" };
        foreach (var (property, value) in filters)
        {
            if (!EntityFields.VideoSetting.TryGetValue(property, out var field))
            {
                throw new OrbisQueryException($"Tipo non supportato: {property}");
            }

            builder.WhereEquals(builder.Column(field), FilterValueConverter.Convert(field.Kind, value));
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video_setting t {builder.WhereClause} ORDER BY t.id;";
        builder.ApplyTo(command);
        return ReadAll(connection, command);
    }

    public VideoSettingEntity? FindById(int id)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video_setting t WHERE t.id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return ReadAll(connection, command).FirstOrDefault();
    }

    public List<VideoSettingEntity> FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration(string platform)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video_setting t " +
            "WHERE t.is_default_configuration = 1 AND t.default_platform_configuration = @platform ORDER BY t.id;";
        command.Parameters.AddWithValue("@platform", platform);
        return ReadAll(connection, command);
    }

    public VideoSettingEntity? FindByTitleAndPlatform(string title, string platform)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video_setting t " +
            "WHERE t.title = @title AND t.default_platform_configuration = @platform LIMIT 1;";
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@platform", platform);
        return ReadAll(connection, command).FirstOrDefault();
    }

    public int Insert(VideoSettingEntity setting)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        var audioSettingId = InsertAudioSetting(connection, transaction, setting.AudioSetting);
        setting.AudioSettingId = audioSettingId;
        setting.Id = InsertSetting(connection, transaction, setting, audioSettingId);
        ReplaceOptions(connection, transaction, setting.Id.Value, setting.VideoSettingsOptions);

        transaction.Commit();
        return setting.Id.Value;
    }

    public void Update(VideoSettingEntity setting)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        var audioSettingId = setting.AudioSetting is null
            ? null
            : UpdateAudioSetting(connection, transaction, setting.AudioSetting, setting.Id);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE video_setting
                SET title = @title,
                    video_codec = @videoCodec,
                    video_codec_name = @videoCodecName,
                    pixel_format = @pixelFormat,
                    video_bitrate = @videoBitrate,
                    video_format = @videoFormat,
                    last_modified = @lastModified,
                    is_default_configuration = @isDefault,
                    default_platform_configuration = @platform,
                    gop_size = @gopSize,
                    video_width = @videoWidth,
                    video_height = @videoHeight,
                    frame_rate = @frameRate,
                    is_video_and_audio_setting_active = @isActive,
                    audio_setting_id = @audioSettingId
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@title", (object?)setting.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("@videoCodec", SqliteValue.From(setting.VideoCodec));
            command.Parameters.AddWithValue("@videoCodecName", (object?)setting.VideoCodecName ?? DBNull.Value);
            command.Parameters.AddWithValue("@pixelFormat", SqliteValue.From(setting.PixelFormat));
            command.Parameters.AddWithValue("@videoBitrate", SqliteValue.From(setting.VideoBitrate));
            command.Parameters.AddWithValue("@videoFormat", (object?)setting.VideoFormat ?? DBNull.Value);
            command.Parameters.AddWithValue("@lastModified", SqliteValue.From(setting.LastModified));
            command.Parameters.AddWithValue("@isDefault", SqliteValue.From(setting.IsDefaultConfiguration));
            command.Parameters.AddWithValue("@platform", (object?)setting.DefaultPlatformConfiguration ?? DBNull.Value);
            command.Parameters.AddWithValue("@gopSize", SqliteValue.From(setting.GopSize));
            command.Parameters.AddWithValue("@videoWidth", SqliteValue.From(setting.VideoWidth));
            command.Parameters.AddWithValue("@videoHeight", SqliteValue.From(setting.VideoHeight));
            command.Parameters.AddWithValue("@frameRate", SqliteValue.From(setting.FrameRate));
            command.Parameters.AddWithValue("@isActive", SqliteValue.From(setting.IsVideoAndAudioSettingActive));
            command.Parameters.AddWithValue("@audioSettingId", audioSettingId is null ? DBNull.Value : audioSettingId.Value);
            command.Parameters.AddWithValue("@id", setting.Id ?? 0);
            command.ExecuteNonQuery();
        }

        if (setting.Id is not null)
        {
            ReplaceOptions(connection, transaction, setting.Id.Value, setting.VideoSettingsOptions);
        }

        transaction.Commit();
    }

    /// <summary>Deletes the setting together with its owned audio row and option rows.</summary>
    public void Delete(int id)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        int? audioSettingId = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT audio_setting_id FROM video_setting WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
            {
                audioSettingId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        Execute(connection, transaction, "DELETE FROM video_settings_options WHERE video_setting_id = @id;", id);
        Execute(connection, transaction, "DELETE FROM video_setting WHERE id = @id;", id);

        if (audioSettingId is not null)
        {
            Execute(connection, transaction, "DELETE FROM audio_setting WHERE id = @id;", audioSettingId.Value);
        }

        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static int? InsertAudioSetting(SqliteConnection connection, SqliteTransaction transaction, AudioSettingEntity? audioSetting)
    {
        if (audioSetting is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audio_setting (audio_codec, audio_bitrate) VALUES (@codec, @bitrate); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@codec", SqliteValue.From(audioSetting.AudioCodec));
        command.Parameters.AddWithValue("@bitrate", SqliteValue.From(audioSetting.AudioBitrate));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int? UpdateAudioSetting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AudioSettingEntity audioSetting,
        int? videoSettingId)
    {
        var existingId = audioSetting.Id
            ?? (videoSettingId is null ? null : LoadAudioSettingId(connection, transaction, videoSettingId.Value));

        if (existingId is not null)
        {
            audioSetting.Id = existingId;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE audio_setting SET audio_codec = @codec, audio_bitrate = @bitrate WHERE id = @id;";
            update.Parameters.AddWithValue("@codec", SqliteValue.From(audioSetting.AudioCodec));
            update.Parameters.AddWithValue("@bitrate", SqliteValue.From(audioSetting.AudioBitrate));
            update.Parameters.AddWithValue("@id", existingId.Value);
            update.ExecuteNonQuery();
            return existingId;
        }

        return InsertAudioSetting(connection, transaction, audioSetting);
    }

    private static int? LoadAudioSettingId(SqliteConnection connection, SqliteTransaction transaction, int videoSettingId)
    {
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT audio_setting_id FROM video_setting WHERE id = @id;";
        find.Parameters.AddWithValue("@id", videoSettingId);
        var existing = find.ExecuteScalar();
        return existing is null or DBNull ? null : Convert.ToInt32(existing, CultureInfo.InvariantCulture);
    }

    private static int InsertSetting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VideoSettingEntity setting,
        int? audioSettingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO video_setting (title, video_codec, video_codec_name, pixel_format, video_bitrate, video_format,
                                       last_modified, is_default_configuration, default_platform_configuration, gop_size,
                                       video_width, video_height, frame_rate,
                                       is_video_and_audio_setting_active, audio_setting_id)
            VALUES (@title, @videoCodec, @videoCodecName, @pixelFormat, @videoBitrate, @videoFormat,
                    @lastModified, @isDefault, @platform, @gopSize,
                    @videoWidth, @videoHeight, @frameRate,
                    @isActive, @audioSettingId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@title", (object?)setting.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("@videoCodec", SqliteValue.From(setting.VideoCodec));
        command.Parameters.AddWithValue("@videoCodecName", (object?)setting.VideoCodecName ?? DBNull.Value);
        command.Parameters.AddWithValue("@pixelFormat", SqliteValue.From(setting.PixelFormat));
        command.Parameters.AddWithValue("@videoBitrate", SqliteValue.From(setting.VideoBitrate));
        command.Parameters.AddWithValue("@videoFormat", (object?)setting.VideoFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("@lastModified", SqliteValue.From(setting.LastModified));
        command.Parameters.AddWithValue("@isDefault", SqliteValue.From(setting.IsDefaultConfiguration));
        command.Parameters.AddWithValue("@platform", (object?)setting.DefaultPlatformConfiguration ?? DBNull.Value);
        command.Parameters.AddWithValue("@gopSize", SqliteValue.From(setting.GopSize));
        command.Parameters.AddWithValue("@videoWidth", SqliteValue.From(setting.VideoWidth));
        command.Parameters.AddWithValue("@videoHeight", SqliteValue.From(setting.VideoHeight));
        command.Parameters.AddWithValue("@frameRate", SqliteValue.From(setting.FrameRate));
        command.Parameters.AddWithValue("@isActive", SqliteValue.From(setting.IsVideoAndAudioSettingActive));
        command.Parameters.AddWithValue("@audioSettingId", audioSettingId is null ? DBNull.Value : audioSettingId.Value);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ReplaceOptions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int videoSettingId,
        IReadOnlyList<VideoSettingsOptionEntity> options)
    {
        Execute(connection, transaction, "DELETE FROM video_settings_options WHERE video_setting_id = @id;", videoSettingId);

        foreach (var option in options)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO video_settings_options (video_setting_id, option_key, option_value) VALUES (@setting, @key, @value);";
            command.Parameters.AddWithValue("@setting", videoSettingId);
            command.Parameters.AddWithValue("@key", (object?)option.Key ?? DBNull.Value);
            command.Parameters.AddWithValue("@value", (object?)option.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static List<VideoSettingEntity> ReadAll(SqliteConnection connection, SqliteCommand command)
    {
        var items = new List<VideoSettingEntity>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new VideoSettingEntity
                {
                    Id = reader.GetInt32(0),                    Title = SqliteValue.ToText(reader.GetValue(1)),
                    VideoCodec = SqliteValue.ToNullableInt32(reader.GetValue(2)),
                    VideoCodecName = SqliteValue.ToText(reader.GetValue(3)),
                    PixelFormat = SqliteValue.ToNullableInt32(reader.GetValue(4)),
                    VideoBitrate = SqliteValue.ToNullableInt32(reader.GetValue(5)),
                    VideoFormat = SqliteValue.ToText(reader.GetValue(6)),
                    LastModified = SqliteValue.ToNullableDateTime(reader.GetValue(7)),
                    IsDefaultConfiguration = reader.IsDBNull(8) ? null : SqliteValue.ToBoolean(reader.GetValue(8)),
                    DefaultPlatformConfiguration = SqliteValue.ToText(reader.GetValue(9)),
                    GopSize = SqliteValue.ToNullableInt32(reader.GetValue(10)),
                    IsVideoAndAudioSettingActive = reader.IsDBNull(11) ? null : SqliteValue.ToBoolean(reader.GetValue(11)),
                    AudioSettingId = SqliteValue.ToNullableInt32(reader.GetValue(12)),
                    VideoWidth = SqliteValue.ToNullableInt32(reader.GetValue(13)),
                    VideoHeight = SqliteValue.ToNullableInt32(reader.GetValue(14)),
                    FrameRate = SqliteValue.ToNullableDouble(reader.GetValue(15))
                });
            }
        }

        foreach (var item in items)
        {
            item.AudioSetting = LoadAudioSetting(connection, item.Id!.Value);
            item.VideoSettingsOptions = LoadOptions(connection, item.Id.Value);
        }

        return items;
    }

    private static AudioSettingEntity? LoadAudioSetting(SqliteConnection connection, int videoSettingId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT a.id, a.audio_codec, a.audio_bitrate FROM audio_setting a " +
            "INNER JOIN video_setting v ON v.audio_setting_id = a.id WHERE v.id = @id;";
        command.Parameters.AddWithValue("@id", videoSettingId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AudioSettingEntity
        {
            Id = reader.IsDBNull(0) ? null : reader.GetInt32(0),
            AudioCodec = SqliteValue.ToNullableInt32(reader.GetValue(1)),
            AudioBitrate = SqliteValue.ToNullableInt32(reader.GetValue(2))
        };
    }

    private static List<VideoSettingsOptionEntity> LoadOptions(SqliteConnection connection, int videoSettingId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT option_key, option_value FROM video_settings_options WHERE video_setting_id = @id ORDER BY rowid;";
        command.Parameters.AddWithValue("@id", videoSettingId);
        var options = new List<VideoSettingsOptionEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            options.Add(new VideoSettingsOptionEntity
            {
                Key = SqliteValue.ToText(reader.GetValue(0)),
                Value = SqliteValue.ToText(reader.GetValue(1))
            });
        }

        return options;
    }
}
