using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Data;

/// <summary>Port of <c>com.orbis.stream.repository.SettingRepository</c>.</summary>
public sealed class SettingRepository
{
    private const string BaseColumns =
        "t.id, t.stream_url, t.stream_key, t.platform_stream_name, t.description, t.video_folder, t.is_active, t.channel_name, "
        + "t.scene_pkid, t.auto_cleanup_enabled, t.auto_cleanup_interval_months, t.auto_cleanup_older_than_months";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SettingRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public List<SettingEntity> FindAll(IReadOnlyDictionary<string, string> filters)
    {
        using var connection = _connectionFactory.Open();

        var builder = new SqlQueryBuilder { TableAlias = "t" };
        foreach (var (property, value) in filters)
        {
            if (!EntityFields.Setting.TryGetValue(property, out var field))
            {
                throw new OrbisQueryException($"Tipo non supportato: {property}");
            }

            builder.WhereEquals(builder.Column(field), FilterValueConverter.Convert(field.Kind, value));
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM setting t {builder.WhereClause} ORDER BY t.id;";
        builder.ApplyTo(command);
        return ReadAll(command);
    }

    public SettingEntity? FindById(int id)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM setting t WHERE t.id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return ReadAll(command).FirstOrDefault();
    }

    public SettingEntity? FindByStreamUrlAndStreamKey(string streamUrl, string streamKey)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM setting t WHERE t.stream_url = @url AND t.stream_key = @key;";
        command.Parameters.AddWithValue("@url", streamUrl);
        command.Parameters.AddWithValue("@key", streamKey);
        return ReadAll(command).FirstOrDefault();
    }

    public int Insert(SettingEntity setting)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO setting (stream_url, stream_key, platform_stream_name, description, video_folder, is_active, channel_name, scene_pkid,
                                 auto_cleanup_enabled, auto_cleanup_interval_months, auto_cleanup_older_than_months)
            VALUES (@streamUrl, @streamKey, @platform, @description, @videoFolder, @isActive, @channelName, @scenePkid,
                    @autoCleanupEnabled, @autoCleanupIntervalMonths, @autoCleanupOlderThanMonths);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@streamUrl", setting.StreamUrl);
        command.Parameters.AddWithValue("@streamKey", setting.StreamKey);
        command.Parameters.AddWithValue("@platform", (object?)setting.PlatformStreamName ?? DBNull.Value);
        command.Parameters.AddWithValue("@description", (object?)setting.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@videoFolder", setting.VideoFolder);
        command.Parameters.AddWithValue("@isActive", SqliteValue.From(setting.IsActive));
        command.Parameters.AddWithValue("@channelName", setting.ChannelName);
        command.Parameters.AddWithValue("@scenePkid", SqliteValue.From(setting.ScenePkid));
        command.Parameters.AddWithValue("@autoCleanupEnabled", setting.AutoCleanupEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@autoCleanupIntervalMonths", setting.AutoCleanupIntervalMonths);
        command.Parameters.AddWithValue("@autoCleanupOlderThanMonths", setting.AutoCleanupOlderThanMonths);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Update(SettingEntity setting)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE setting
            SET stream_url = COALESCE(@streamUrl, stream_url),
                stream_key = COALESCE(@streamKey, stream_key),
                platform_stream_name = COALESCE(@platform, platform_stream_name),
                description = COALESCE(@description, description),
                video_folder = COALESCE(@videoFolder, video_folder),
                is_active = COALESCE(@isActive, is_active),
                channel_name = COALESCE(@channelName, channel_name),
                scene_pkid = COALESCE(@scenePkid, scene_pkid),
                auto_cleanup_enabled = COALESCE(@autoCleanupEnabled, auto_cleanup_enabled),
                auto_cleanup_interval_months = COALESCE(@autoCleanupIntervalMonths, auto_cleanup_interval_months),
                auto_cleanup_older_than_months = COALESCE(@autoCleanupOlderThanMonths, auto_cleanup_older_than_months)
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@streamUrl", (object?)setting.StreamUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@streamKey", (object?)setting.StreamKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@platform", (object?)setting.PlatformStreamName ?? DBNull.Value);
        command.Parameters.AddWithValue("@description", (object?)setting.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@videoFolder", (object?)setting.VideoFolder ?? DBNull.Value);
        command.Parameters.AddWithValue("@isActive", SqliteValue.From(setting.IsActive));
        command.Parameters.AddWithValue("@channelName", (object?)setting.ChannelName ?? DBNull.Value);
        command.Parameters.AddWithValue("@scenePkid", SqliteValue.From(setting.ScenePkid));
        command.Parameters.AddWithValue("@autoCleanupEnabled", setting.AutoCleanupEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@autoCleanupIntervalMonths", setting.AutoCleanupIntervalMonths);
        command.Parameters.AddWithValue("@autoCleanupOlderThanMonths", setting.AutoCleanupOlderThanMonths);
        command.Parameters.AddWithValue("@id", setting.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM setting WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static List<SettingEntity> ReadAll(SqliteCommand command)
    {
        var items = new List<SettingEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SettingEntity
            {
                Id = reader.GetInt32(0),
                StreamUrl = reader.GetString(1),
                StreamKey = reader.GetString(2),
                PlatformStreamName = SqliteValue.ToText(reader.GetValue(3)),
                Description = SqliteValue.ToText(reader.GetValue(4)),
                VideoFolder = reader.GetString(5),
                IsActive = reader.IsDBNull(6) ? null : SqliteValue.ToBoolean(reader.GetValue(6)),
                ChannelName = reader.GetString(7),
                ScenePkid = SqliteValue.ToNullableInt64(reader.GetValue(8)),
                AutoCleanupEnabled = reader.IsDBNull(9) ? false : SqliteValue.ToBoolean(reader.GetValue(9)),
                AutoCleanupIntervalMonths = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                AutoCleanupOlderThanMonths = reader.IsDBNull(11) ? 0 : reader.GetInt32(11)
            });
        }

        return items;
    }
}

/// <summary>Port of <c>com.orbis.stream.repository.VideoLiveHistoryRepository</c>.</summary>
public sealed class VideoLiveHistoryRepository
{
    private const string BaseColumns =
        "t.pkid, t.folder_of_video_to_stream, t.local_date_time_start_live, t.stream_url, t.stream_key, t.platform_stream_name, t.user_name";

    private readonly SqliteConnectionFactory _connectionFactory;

    public VideoLiveHistoryRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public long Insert(VideoLiveHistoryEntity history)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO video_live_history (folder_of_video_to_stream, local_date_time_start_live, stream_url, stream_key, platform_stream_name, user_name)
            VALUES (@folder, @start, @streamUrl, @streamKey, @platform, @userName);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@folder", history.FolderOfVideoToStream);
        command.Parameters.AddWithValue("@start", SqliteValue.From(history.LocalDateTimeStartLive));
        command.Parameters.AddWithValue("@streamUrl", history.StreamUrl);
        command.Parameters.AddWithValue("@streamKey", history.StreamKey);
        command.Parameters.AddWithValue("@platform", history.PlatformStreamName);
        command.Parameters.AddWithValue("@userName", (object?)history.UserName ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public VideoLiveHistoryEntity? FindByFolderOfVideoToStreamAndLocalDateTimeStartLive(string folder, DateTime startLive)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video_live_history t " +
            "WHERE t.folder_of_video_to_stream = @folder AND t.local_date_time_start_live = @start " +
            "ORDER BY t.pkid DESC;";
        command.Parameters.AddWithValue("@folder", folder);
        command.Parameters.AddWithValue("@start", SqliteValue.From(startLive));
        return ReadAll(command).FirstOrDefault();
    }

    public VideoLiveHistoryEntity? FindByPkid(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video_live_history t WHERE t.pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        return ReadAll(command).FirstOrDefault();
    }

    /// <summary>
    /// The lives that started before the given date, oldest first. The rows come back whole because
    /// deleting them is a walk over their videos, and a walk needs to know which live each one
    /// belongs to.
    /// </summary>
    public List<VideoLiveHistoryEntity> FindOlderThan(DateTime threshold)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video_live_history t WHERE t.local_date_time_start_live < @threshold ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@threshold", SqliteValue.From(threshold));
        return ReadAll(command);
    }

    /// <summary>
    /// Deletes the live history row on its own. Its videos go first: the foreign key of
    /// <c>video.video_live_history_pkid</c> has no cascade, so a row with videos under it cannot be
    /// deleted while they are there.
    /// </summary>
    public void DeleteRow(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM video_live_history WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();
    }

    private static List<VideoLiveHistoryEntity> ReadAll(SqliteCommand command)
    {
        var items = new List<VideoLiveHistoryEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new VideoLiveHistoryEntity
            {
                Pkid = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                FolderOfVideoToStream = reader.GetString(1),
                LocalDateTimeStartLive = SqliteValue.ToNullableDateTime(reader.GetValue(2)) ?? default,
                StreamUrl = reader.GetString(3),
                StreamKey = reader.GetString(4),
                PlatformStreamName = reader.GetString(5),
                UserName = SqliteValue.ToText(reader.GetValue(6))
            });
        }

        return items;
    }
}
