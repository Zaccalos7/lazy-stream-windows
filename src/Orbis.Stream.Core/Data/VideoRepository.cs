using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Data;

/// <summary>Port of <c>com.orbis.stream.repository.VideoRepository</c>.</summary>
public sealed class VideoRepository
{
    private const string BaseColumns =
        "t.pkid, t.name, t.video_path, t.extension, t.live_status, t.last_time_stamp_before_stop, " +
        "t.message, t.should_be_stop, t.start_date_live, t.channel_name, t.video_live_history_pkid, t.video_setting_id, " +
        "t.source_kind, t.source_target, t.scene_pkid, t.x, t.y, t.width, t.height, t.audio_enabled";

    private readonly SqliteConnectionFactory _connectionFactory;

    public VideoRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public PagedResult<VideoEntity> FindPaged(IReadOnlyDictionary<string, string> filters, PageRequest page)
    {
        using var connection = _connectionFactory.Open();

        var builder = new SqlQueryBuilder { TableAlias = "t" };
        var needsHistoryJoin = false;

        foreach (var (property, value) in filters)
        {
            if (property.Equals("platform", StringComparison.Ordinal))
            {
                // The compiled React build sends "platform" as filter of this endpoint; it maps to
                // the platform name stored on the related live history row.
                builder.Join = "LEFT JOIN video_live_history h ON h.pkid = t.video_live_history_pkid";
                builder.WhereEquals("h.platform_stream_name", value);
                needsHistoryJoin = true;
                continue;
            }

            if (!EntityFields.Video.TryGetValue(property, out var field))
            {
                throw new OrbisQueryException($"Tipo non supportato: {property}");
            }

            builder.WhereEquals(builder.Column(field), FilterValueConverter.Convert(field.Kind, value));
        }

        foreach (var sort in page.Sorts)
        {
            if (!EntityFields.Video.TryGetValue(sort.Property, out var field))
            {
                throw new OrbisQueryException($"Tipo non supportato: {sort.Property}");
            }

            builder.OrderBy(builder.Column(field), sort.Descending);
        }

        var where = builder.WhereClause;
        var join = needsHistoryJoin ? builder.Join! : string.Empty;

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM video t {join} {where};";
        builder.ApplyTo(countCommand);
        var total = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        builder.ApplyPaging(page.Page, page.Size);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video t {join} {where} {builder.OrderByClause} " +
            $"LIMIT {builder.Limit} OFFSET {builder.Offset};";
        builder.ApplyTo(command);

        var items = new List<VideoEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(Map(reader));
        }

        return new PagedResult<VideoEntity>(items, page.Page, page.Size, total);
    }

    public List<VideoEntity> FindByLiveHistoryId(long videoLiveHistoryPkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video t WHERE t.video_live_history_pkid = @pkid ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@pkid", videoLiveHistoryPkid);
        return ReadAll(command);
    }

    public List<VideoEntity> FindByLiveStatus(LiveStatus liveStatus)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video t WHERE t.live_status = @status ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@status", liveStatus.ToStorageValue());
        return ReadAll(command);
    }

    public List<VideoEntity> FindByLiveStatusAndChannelName(LiveStatus liveStatus, string channelName)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video t WHERE t.live_status = @status AND t.channel_name = @channel ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@status", liveStatus.ToStorageValue());
        command.Parameters.AddWithValue("@channel", channelName);
        return ReadAll(command);
    }

    public VideoEntity? FindByPkid(int pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video t WHERE t.pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        return ReadAll(command).FirstOrDefault();
    }

    public VideoEntity? FindByVideoPathAndVideoLiveHistoryPkid(string videoPath, long videoLiveHistoryPkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BaseColumns} FROM video t WHERE t.video_path = @path AND t.video_live_history_pkid = @pkid;";
        command.Parameters.AddWithValue("@path", videoPath);
        command.Parameters.AddWithValue("@pkid", videoLiveHistoryPkid);
        return ReadAll(command).FirstOrDefault();
    }

    /// <summary>
    /// The rows a live streaming a canvas has, in stacking order. The playlist already comes back
    /// ordered by pkid, and this is the same order the scene items were written in, so the first
    /// row is the base the others are laid over.
    /// </summary>
    public IReadOnlyList<VideoEntity> FindByScenePkid(long scenePkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM video t WHERE t.scene_pkid = @scene ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@scene", scenePkid);
        return ReadAll(command);
    }

    public int Insert(VideoEntity video)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO video (name, video_path, extension, live_status, last_time_stamp_before_stop, message,
                               should_be_stop, start_date_live, channel_name, video_live_history_pkid, video_setting_id,
                               source_kind, source_target, scene_pkid, x, y, width, height, audio_enabled)
            VALUES (@name, @path, @extension, @liveStatus, @lastTimeStamp, @message, @shouldBeStop, @startDateLive, @channelName, @history, @setting,
                    @sourceKind, @sourceTarget, @scenePkid, @x, @y, @width, @height, @audioEnabled);
            SELECT last_insert_rowid();
            """;
        Bind(command, video);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Update(VideoEntity video)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE video
            SET name = @name,
                video_path = @path,
                extension = @extension,
                live_status = @liveStatus,
                last_time_stamp_before_stop = @lastTimeStamp,
                message = @message,
                should_be_stop = @shouldBeStop,
                start_date_live = @startDateLive,
                channel_name = @channelName,
                video_live_history_pkid = @history,
                video_setting_id = @setting,
                source_kind = @sourceKind,
                source_target = @sourceTarget,
                scene_pkid = @scenePkid,
                x = @x,
                y = @y,
                width = @width,
                height = @height,
                audio_enabled = @audioEnabled
            WHERE pkid = @pkid;
            """;
        Bind(command, video);
        command.Parameters.AddWithValue("@pkid", video.Pkid);
        command.ExecuteNonQuery();
    }

    public void SetStopFlag(int pkid, bool shouldBeStop)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video SET should_be_stop = @shouldBeStop WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@shouldBeStop", shouldBeStop ? 1 : 0);
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();
    }

    public void Delete(int pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM video WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();
    }

    public void SetVideoSetting(int pkid, int? videoSettingId)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video SET video_setting_id = @setting WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@setting", videoSettingId is null ? DBNull.Value : videoSettingId.Value);
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, VideoEntity video)
    {
        command.Parameters.AddWithValue("@name", video.Name);
        command.Parameters.AddWithValue("@path", video.VideoPath);
        command.Parameters.AddWithValue("@extension", video.Extension);
        command.Parameters.AddWithValue("@liveStatus", video.LiveStatus.ToStorageValue());
        command.Parameters.AddWithValue("@lastTimeStamp", video.LastTimeStampBeforeStop);
        command.Parameters.AddWithValue("@message", (object?)video.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("@shouldBeStop", video.ShouldBeStop ? 1 : 0);
        command.Parameters.AddWithValue("@startDateLive", SqliteValue.From(video.StartDateLive));
        command.Parameters.AddWithValue("@channelName", video.ChannelName);
        command.Parameters.AddWithValue("@history", video.VideoLiveHistoryId is null ? DBNull.Value : video.VideoLiveHistoryId.Value);
        command.Parameters.AddWithValue("@setting", video.VideoSettingId is null ? DBNull.Value : video.VideoSettingId.Value);
        command.Parameters.AddWithValue("@sourceKind", (int)video.SourceKind);
        command.Parameters.AddWithValue("@sourceTarget", SqliteValue.From(video.SourceTarget));
        command.Parameters.AddWithValue("@scenePkid", SqliteValue.From(video.ScenePkid));
        command.Parameters.AddWithValue("@x", SqliteValue.From(video.X));
        command.Parameters.AddWithValue("@y", SqliteValue.From(video.Y));
        command.Parameters.AddWithValue("@width", SqliteValue.From(video.Width));
        command.Parameters.AddWithValue("@height", SqliteValue.From(video.Height));
        command.Parameters.AddWithValue("@audioEnabled", video.AudioEnabled ? 1 : 0);
    }

    private static List<VideoEntity> ReadAll(SqliteCommand command)
    {
        var items = new List<VideoEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(Map(reader));
        }

        return items;
    }

    private static VideoEntity Map(SqliteDataReader reader) => new()
    {
        Pkid = reader.GetInt32(0),
        Name = reader.GetString(1),
        VideoPath = reader.GetString(2),
        Extension = reader.GetString(3),
        LiveStatus = LiveStatusExtensions.FromStorage(reader.GetValue(4)),
        LastTimeStampBeforeStop = SqliteValue.ToInt64(reader.GetValue(5)),
        Message = SqliteValue.ToText(reader.GetValue(6)),
        ShouldBeStop = SqliteValue.ToBoolean(reader.GetValue(7)),
        StartDateLive = SqliteValue.ToNullableDateTime(reader.GetValue(8)),
        ChannelName = reader.GetString(9),
        VideoLiveHistoryId = SqliteValue.ToNullableInt64(reader.GetValue(10)),
        VideoSettingId = SqliteValue.ToNullableInt32(reader.GetValue(11)),
        SourceKind = SourceKindExtensions.TryParse(SqliteValue.ToText(reader.GetValue(12)), out var kind)
            ? kind
            : SourceKind.File,
        SourceTarget = SqliteValue.ToText(reader.GetValue(13)),
        ScenePkid = SqliteValue.ToNullableInt64(reader.GetValue(14)),
        X = SqliteValue.ToNullableInt32(reader.GetValue(15)),
        Y = SqliteValue.ToNullableInt32(reader.GetValue(16)),
        Width = SqliteValue.ToNullableInt32(reader.GetValue(17)),
        Height = SqliteValue.ToNullableInt32(reader.GetValue(18)),
        AudioEnabled = SqliteValue.ToBoolean(reader.GetValue(19))
    };
}

internal static class SqliteValue
{
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    public static object From(DateTime? value) =>
        value is null ? DBNull.Value : value.Value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public static object From(DateTime value) => value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public static object From(int? value) => value is null ? DBNull.Value : value.Value;

    public static object From(long? value) => value is null ? DBNull.Value : value.Value;

    public static object From(bool? value) => value is null ? DBNull.Value : value.Value ? 1L : 0L;

    public static object From(double? value) => value is null ? DBNull.Value : value.Value;

    public static object From(string? value) => value is null ? DBNull.Value : value;

    public static string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        var other => Convert.ToString(other, CultureInfo.InvariantCulture)
    };

    public static long ToInt64(object? value) => value switch
    {
        null or DBNull => 0L,
        long number => number,
        int number => number,
        var other => Convert.ToInt64(other, CultureInfo.InvariantCulture)
    };

    public static long? ToNullableInt64(object? value) => value switch
    {
        null or DBNull => null,
        _ => ToInt64(value)
    };

    public static int? ToNullableInt32(object? value) => value switch
    {
        null or DBNull => null,
        _ => (int)ToInt64(value)
    };

    public static double? ToNullableDouble(object? value) => value switch
    {
        null or DBNull => null,
        double number => number,
        long number => number,
        int number => number,
        var other => Convert.ToDouble(other, CultureInfo.InvariantCulture)
    };

    public static bool ToBoolean(object? value) => value switch
    {
        null or DBNull => false,
        bool flag => flag,
        long number => number != 0,
        int number => number != 0,
        string text => text is "1" or "true" or "True" or "TRUE",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    public static DateTime? ToNullableDateTime(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dateTime => dateTime,
        long number => DateTime.FromOADate(number),
        string text => DateTimeParsing.Parse(text),
        _ => null
    };
}
