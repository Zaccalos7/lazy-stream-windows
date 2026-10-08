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
        "t.source_kind, t.source_target, t.scene_pkid, t.x, t.y, t.width, t.height, t.audio_enabled, t.duration_milliseconds, " +
        "t.source_width, t.source_height, t.volume";

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
                               source_kind, source_target, scene_pkid, x, y, width, height, audio_enabled, duration_milliseconds,
                               source_width, source_height, volume)
            VALUES (@name, @path, @extension, @liveStatus, @lastTimeStamp, @message, @shouldBeStop, @startDateLive, @channelName, @history, @setting,
                    @sourceKind, @sourceTarget, @scenePkid, @x, @y, @width, @height, @audioEnabled, @duration,
                    @sourceWidth, @sourceHeight, @volume);
            SELECT last_insert_rowid();
            """;
        Bind(command, video);
        command.Parameters.AddWithValue("@volume", video.Volume);
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
                audio_enabled = @audioEnabled,
                duration_milliseconds = @duration,
                source_width = @sourceWidth,
                source_height = @sourceHeight
            WHERE pkid = @pkid;
            """;
        Bind(command, video);
        command.Parameters.AddWithValue("@pkid", video.Pkid);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The volume has a writer of its own and <see cref="Update"/> leaves it alone: the streaming
    /// loop writes the rows of a live it holds in memory every time their status changes, and a
    /// volume set from the preview in between would be written back to what it was.
    /// </summary>
    public void SetVolume(int pkid, int volume)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video SET volume = @volume WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@volume", volume);
        command.Parameters.AddWithValue("@pkid", pkid);
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

    /// <summary>
    /// The live page: one row per live. A folder playlist shows the video it got to (the one on air,
    /// else the last one that was played, else the first one), with where it stands in the playlist.
    /// A canvas shows its base source, the first one with a picture: that is the row its ffmpeg is
    /// registered under, so it is the row a stop has to be addressed to.
    /// <para>The status of a playlist is its own, not the one of the video shown: LIVE while any video
    /// is on air, ENDED once the last one was streamed through, else the one of the video it got to.
    /// The status filter reads the same value.</para>
    /// <para>The platform filter reads the platform the live was started for, which the live history
    /// keeps (<c>twitch</c>, <c>youtube</c>, <c>kick</c>, <c>facebook</c>), in whatever case it was stored.</para>
    /// </summary>
    public PagedResult<LiveRowEntity> FindLivePage(
        LiveStatus? liveStatus, string? channelName, int page, int size, long? videoLiveHistoryPkid = null, string? platform = null)
    {
        const string Grouped =
            """
            SELECT v.*,
                   ROW_NUMBER() OVER (PARTITION BY v.grp ORDER BY v.pkid) AS position,
                   COUNT(*) OVER (PARTITION BY v.grp) AS total,
                   ROW_NUMBER() OVER (PARTITION BY v.grp ORDER BY
                       CASE WHEN v.scene_pkid IS NOT NULL THEN (CASE WHEN v.source_kind = @microphone THEN 1 ELSE 0 END)
                            WHEN v.live_status = @live THEN 0 WHEN v.live_status = @offline THEN 2 ELSE 1 END,
                       CASE WHEN v.scene_pkid IS NOT NULL OR v.live_status = @offline THEN NULL ELSE v.start_date_live END DESC,
                       CASE WHEN v.scene_pkid IS NOT NULL OR v.live_status = @offline THEN v.pkid ELSE -v.pkid END) AS pick,
                   CASE
                       WHEN MAX(CASE WHEN v.live_status = @live THEN 1 ELSE 0 END) OVER (PARTITION BY v.grp) = 1 THEN @live
                       WHEN FIRST_VALUE(v.live_status) OVER (PARTITION BY v.grp ORDER BY v.pkid DESC) = @ended THEN @ended
                   END AS group_status
            FROM (SELECT x.*,
                         CASE WHEN x.video_live_history_pkid IS NOT NULL
                              THEN 'h' || x.video_live_history_pkid ELSE 'v' || x.pkid END AS grp
                  FROM video x) v
            """;

        var where = "WHERE t.pick = 1"
            + (liveStatus is null ? string.Empty : " AND COALESCE(t.group_status, t.live_status) = @status")
            + (string.IsNullOrEmpty(channelName) ? string.Empty : " AND t.channel_name = @channel")
            + (videoLiveHistoryPkid is null ? string.Empty : " AND t.video_live_history_pkid = @history")
            + (string.IsNullOrEmpty(platform)
                ? string.Empty
                : " AND t.video_live_history_pkid IN (SELECT h.pkid FROM video_live_history h WHERE lower(h.platform_stream_name) = @platform)");

        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("@live", LiveStatus.Live.ToStorageValue());
            command.Parameters.AddWithValue("@offline", LiveStatus.Offline.ToStorageValue());
            command.Parameters.AddWithValue("@ended", LiveStatus.Ended.ToStorageValue());
            command.Parameters.AddWithValue("@microphone", (int)SourceKind.Microphone);
            if (liveStatus is { } status)
            {
                command.Parameters.AddWithValue("@status", status.ToStorageValue());
            }

            if (!string.IsNullOrEmpty(channelName))
            {
                command.Parameters.AddWithValue("@channel", channelName);
            }

            if (videoLiveHistoryPkid is { } history)
            {
                command.Parameters.AddWithValue("@history", history);
            }

            if (!string.IsNullOrEmpty(platform))
            {
                command.Parameters.AddWithValue("@platform", platform.Trim().ToLowerInvariant());
            }
        }

        using var connection = _connectionFactory.Open();

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM ({Grouped}) t {where};";
        Bind(countCommand);
        var total = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT t.position, t.total, COALESCE(t.group_status, t.live_status), {BaseColumns} FROM ({Grouped}) t {where} " +
            "ORDER BY t.start_date_live DESC, t.pkid DESC LIMIT @size OFFSET @offset;";
        Bind(command);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@offset", (long)page * size);

        var items = new List<LiveRowEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var position = reader.GetInt32(0);
            var rowTotal = reader.GetInt32(1);
            var status = LiveStatusExtensions.FromStorage(reader.GetValue(2));
            items.Add(new LiveRowEntity(
                Map(reader, 3), position, rowTotal, status));
        }

        return new PagedResult<LiveRowEntity>(items, page, size, total);
    }

    public void DeleteByLiveHistoryId(long videoLiveHistoryPkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM video WHERE video_live_history_pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", videoLiveHistoryPkid);
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

    /// <summary>Every row that streams with this setting, whatever live it belongs to.</summary>
    public IReadOnlyList<int> FindPkidsByVideoSettingId(int videoSettingId)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.pkid FROM video t WHERE t.video_setting_id = @setting ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@setting", videoSettingId);

        var pkids = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            pkids.Add(reader.GetInt32(0));
        }

        return pkids;
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
        command.Parameters.AddWithValue("@duration", video.DurationMilliseconds is null ? DBNull.Value : video.DurationMilliseconds.Value);
        command.Parameters.AddWithValue("@sourceWidth", SqliteValue.From(video.SourceWidth));
        command.Parameters.AddWithValue("@sourceHeight", SqliteValue.From(video.SourceHeight));
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

private static VideoEntity Map(SqliteDataReader reader, int offset = 0) => new()
    {
        Pkid = reader.GetInt32(offset + 0),
        Name = reader.GetString(offset + 1),
        VideoPath = reader.GetString(offset + 2),
        Extension = reader.GetString(offset + 3),
        LiveStatus = LiveStatusExtensions.FromStorage(reader.GetValue(offset + 4)),
        LastTimeStampBeforeStop = SqliteValue.ToInt64(reader.GetValue(offset + 5)),
        Message = SqliteValue.ToText(reader.GetValue(offset + 6)),
        ShouldBeStop = SqliteValue.ToBoolean(reader.GetValue(offset + 7)),
        StartDateLive = SqliteValue.ToNullableDateTime(reader.GetValue(offset + 8)),
        ChannelName = reader.GetString(offset + 9),
        VideoLiveHistoryId = SqliteValue.ToNullableInt64(reader.GetValue(offset + 10)),
        VideoSettingId = SqliteValue.ToNullableInt32(reader.GetValue(offset + 11)),
        SourceKind = SourceKindExtensions.TryParse(SqliteValue.ToText(reader.GetValue(offset + 12)), out var kind)
            ? kind
            : SourceKind.File,
        SourceTarget = SqliteValue.ToText(reader.GetValue(offset + 13)),
        ScenePkid = SqliteValue.ToNullableInt64(reader.GetValue(offset + 14)),
        X = SqliteValue.ToNullableInt32(reader.GetValue(offset + 15)),
        Y = SqliteValue.ToNullableInt32(reader.GetValue(offset + 16)),
        Width = SqliteValue.ToNullableInt32(reader.GetValue(offset + 17)),
        Height = SqliteValue.ToNullableInt32(reader.GetValue(offset + 18)),
        AudioEnabled = SqliteValue.ToBoolean(reader.GetValue(offset + 19)),
        DurationMilliseconds = SqliteValue.ToNullableInt64(reader.GetValue(offset + 20)),
        SourceWidth = SqliteValue.ToNullableInt32(reader.GetValue(offset + 21)),
        SourceHeight = SqliteValue.ToNullableInt32(reader.GetValue(offset + 22)),
        Volume = SqliteValue.ToNullableInt32(reader.GetValue(offset + 23)) ?? 100
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

/// <summary>A row of the live page: a video, or the video a playlist got to, with where it stands
/// and the status of the whole playlist (the video's own one for a single file).</summary>
public sealed record LiveRowEntity(VideoEntity Video, int Position, int Total, LiveStatus Status);
