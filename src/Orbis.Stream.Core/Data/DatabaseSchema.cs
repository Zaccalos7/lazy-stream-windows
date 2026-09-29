using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Data;

/// <summary>
/// Creates (and, like Hibernate's <c>ddl-auto: update</c>, keeps in sync) the schema.
/// Table and column names follow Spring Boot's <c>CamelCaseToUnderscoresNamingStrategy</c>
/// so that a <c>stream.db</c> produced by the Java version keeps working unchanged.
/// </summary>
public static class DatabaseSchema
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>> Expected =
        new Dictionary<string, IReadOnlyList<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            ["video_live_history"] =
            [
                Column("pkid", "INTEGER", "INTEGER PRIMARY KEY AUTOINCREMENT", nullable: false),
                Column("folder_of_video_to_stream", "TEXT", "TEXT NOT NULL", nullable: false),
                Column("local_date_time_start_live", "TIMESTAMP", "TIMESTAMP NOT NULL", nullable: false),
                Column("stream_url", "VARCHAR(512)", "VARCHAR(512) DEFAULT 'N/A' NOT NULL", nullable: false),
                Column("stream_key", "VARCHAR(512)", "VARCHAR(512) DEFAULT 'N/A' NOT NULL", nullable: false),
                Column("platform_stream_name", "VARCHAR(255)", "VARCHAR(255) DEFAULT 'N/A' NOT NULL", nullable: false),
                Column("user_name", "VARCHAR(255)", "VARCHAR(255)", nullable: true)
            ],
            ["audio_setting"] =
            [
                Column("id", "INTEGER", "INTEGER PRIMARY KEY AUTOINCREMENT", nullable: false),
                Column("audio_codec", "INTEGER", "INTEGER", nullable: true),
                Column("audio_bitrate", "INTEGER", "INTEGER", nullable: true)
            ],
            ["video_setting"] =
            [
                Column("id", "INTEGER", "INTEGER PRIMARY KEY AUTOINCREMENT", nullable: false),
                Column("title", "VARCHAR(255)", "varchar(255) default 'Title'", nullable: true),
                Column("video_codec", "INTEGER", "INTEGER", nullable: true),
                Column("video_codec_name", "VARCHAR(255)", "VARCHAR(255)", nullable: true),
                Column("pixel_format", "INTEGER", "INTEGER", nullable: true),
                Column("video_bitrate", "INTEGER", "INTEGER", nullable: true),
                Column("video_format", "VARCHAR(255)", "VARCHAR(255)", nullable: true),
                Column("last_modified", "TIMESTAMP", "TIMESTAMP", nullable: true),
                Column("is_default_configuration", "BOOLEAN", "BOOLEAN DEFAULT 'False'", nullable: true),
                Column("default_platform_configuration", "VARCHAR(255)", "VARCHAR(255) DEFAULT 'Custom'", nullable: true),
                Column("gop_size", "INTEGER", "INTEGER DEFAULT '2' NOT NULL", nullable: false),
                Column("is_video_and_audio_setting_active", "BOOLEAN", "BOOLEAN DEFAULT 'False' NOT NULL", nullable: false),
                Column("audio_setting_id", "INTEGER", "INTEGER", nullable: true)
            ],
            ["video_settings_options"] =
            [
                Column("video_setting_id", "INTEGER", "INTEGER", nullable: true),
                Column("option_key", "VARCHAR(255)", "VARCHAR(255)", nullable: true),
                Column("option_value", "VARCHAR(255)", "VARCHAR(255)", nullable: true)
            ],
            ["setting"] =
            [
                Column("id", "INTEGER", "INTEGER PRIMARY KEY AUTOINCREMENT", nullable: false),
                Column("stream_url", "VARCHAR(512)", "VARCHAR(512) NOT NULL", nullable: false),
                Column("stream_key", "VARCHAR(512)", "VARCHAR(512) NOT NULL", nullable: false),
                Column("platform_stream_name", "VARCHAR(255)", "VARCHAR(255)", nullable: true),
                Column("description", "TEXT", "TEXT", nullable: true),
                Column("video_folder", "VARCHAR(255)", "VARCHAR(255) DEFAULT '/' NOT NULL", nullable: false),
                Column("is_active", "BOOLEAN", "BOOLEAN DEFAULT 'false'", nullable: true),
                Column("channel_name", "TEXT", "TEXT DEFAULT 'Zingy' NOT NULL", nullable: false)
            ],
            ["video"] =
            [
                Column("pkid", "INTEGER", "INTEGER PRIMARY KEY AUTOINCREMENT", nullable: false),
                Column("name", "VARCHAR(255)", "VARCHAR(255) NOT NULL", nullable: false),
                Column("video_path", "VARCHAR(255)", "VARCHAR(255) NOT NULL", nullable: false),
                Column("extension", "VARCHAR(255)", "VARCHAR(255) NOT NULL", nullable: false),
                Column("live_status", "INTEGER", "INTEGER DEFAULT '1' NOT NULL", nullable: false),
                Column("last_time_stamp_before_stop", "BIGINT", "BIGINT DEFAULT '0'", nullable: true),
                Column("message", "TEXT", "TEXT", nullable: true),
                Column("should_be_stop", "BOOLEAN", "BOOLEAN DEFAULT 'false'", nullable: true),
                Column("start_date_live", "TIMESTAMP", "TIMESTAMP", nullable: true),
                Column("channel_name", "VARCHAR(512)", "VARCHAR(512) DEFAULT 'Zingy' NOT NULL", nullable: false),
                Column("video_live_history_pkid", "BIGINT", "BIGINT", nullable: true),
                Column("video_setting_id", "INTEGER", "INTEGER", nullable: true)
            ]
        };

    private static readonly IReadOnlyList<string> CreateTableStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS video_live_history (
            pkid INTEGER PRIMARY KEY AUTOINCREMENT,
            folder_of_video_to_stream TEXT NOT NULL,
            local_date_time_start_live TIMESTAMP NOT NULL,
            stream_url VARCHAR(512) DEFAULT 'N/A' NOT NULL,
            stream_key VARCHAR(512) DEFAULT 'N/A' NOT NULL,
            platform_stream_name VARCHAR(255) DEFAULT 'N/A' NOT NULL,
            user_name VARCHAR(255)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS audio_setting (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            audio_codec INTEGER,
            audio_bitrate INTEGER
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS video_setting (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title VARCHAR(255) DEFAULT 'Title',
            video_codec INTEGER,
            video_codec_name VARCHAR(255),
            pixel_format INTEGER,
            video_bitrate INTEGER,
            video_format VARCHAR(255),
            last_modified TIMESTAMP,
            is_default_configuration BOOLEAN DEFAULT 'False',
            default_platform_configuration VARCHAR(255) DEFAULT 'Custom',
            gop_size INTEGER DEFAULT '2' NOT NULL,
            is_video_and_audio_setting_active BOOLEAN DEFAULT 'False' NOT NULL,
            audio_setting_id INTEGER,
            FOREIGN KEY (audio_setting_id) REFERENCES audio_setting (id)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS video_settings_options (
            video_setting_id INTEGER,
            option_key VARCHAR(255),
            option_value VARCHAR(255)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS setting (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            stream_url VARCHAR(512) NOT NULL,
            stream_key VARCHAR(512) NOT NULL,
            platform_stream_name VARCHAR(255),
            description TEXT,
            video_folder VARCHAR(255) DEFAULT '/' NOT NULL,
            is_active BOOLEAN DEFAULT 'false',
            channel_name TEXT DEFAULT 'Zingy' NOT NULL,
            CONSTRAINT uk_setting_stream_url_stream_key UNIQUE (stream_url, stream_key)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS video (
            pkid INTEGER PRIMARY KEY AUTOINCREMENT,
            name VARCHAR(255) NOT NULL,
            video_path VARCHAR(255) NOT NULL,
            extension VARCHAR(255) NOT NULL,
            live_status INTEGER DEFAULT '1' NOT NULL,
            last_time_stamp_before_stop BIGINT DEFAULT '0',
            message TEXT,
            should_be_stop BOOLEAN DEFAULT 'false',
            start_date_live TIMESTAMP,
            channel_name VARCHAR(512) DEFAULT 'Zingy' NOT NULL,
            video_live_history_pkid BIGINT,
            video_setting_id INTEGER,
            FOREIGN KEY (video_live_history_pkid) REFERENCES video_live_history (pkid),
            FOREIGN KEY (video_setting_id) REFERENCES video_setting (id) ON DELETE SET NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_video_live_status_channel ON video (live_status, channel_name)",
        "CREATE INDEX IF NOT EXISTS idx_video_live_history ON video (video_live_history_pkid)",
        "CREATE INDEX IF NOT EXISTS idx_video_setting_default ON video_setting (is_default_configuration, default_platform_configuration)",
        "CREATE INDEX IF NOT EXISTS idx_video_settings_options_setting ON video_settings_options (video_setting_id)"
    ];

    public static void EnsureCreated(SqliteConnection connection, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in CreateTableStatements)
        {
            Execute(connection, statement);
        }

        foreach (var (table, columns) in Expected)
        {
            var existing = ReadColumns(connection, table);
            foreach (var column in columns)
            {
                if (!existing.Contains(column.Name))
                {
                    var alter = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Type}";
                    if (!column.Nullable)
                    {
                        alter += " NOT NULL";
                    }

                    if (!string.IsNullOrEmpty(column.DefaultValue))
                    {
                        alter += $" DEFAULT {column.DefaultValue}";
                    }

                    Execute(connection, alter);
                    logger?.LogInformation("Added missing column {Table}.{Column}", table, column.Name);
                }
            }
        }
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static ColumnDefinition Column(string name, string type, string createClause, bool nullable) =>
        new(name, type, nullable, ExtractDefault(createClause));

    private static string? ExtractDefault(string createClause)
    {
        var index = createClause.IndexOf(" DEFAULT ", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = createClause[(index + " DEFAULT ".Length)..].Trim();
        if (value.EndsWith(" NOT NULL", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^" NOT NULL".Length].Trim();
        }

        return value;
    }

    private sealed record ColumnDefinition(string Name, string Type, bool Nullable, string? DefaultValue);
}
