using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Hosting;

namespace Orbis.Stream.Tests;

/// <summary>Creates a throwaway SQLite database with the production schema.</summary>
public sealed class TemporaryDatabase : IDisposable
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public TemporaryDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "orbis-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabasePath = Path.Combine(Directory, "orbis-stream.db");
        _connectionFactory = new SqliteConnectionFactory(DatabasePath);

        using (var connection = _connectionFactory.Open())
        {
            DatabaseSchema.EnsureCreated(connection, NullLogger.Instance);
        }

        // The production startup inserts the default video settings, they are referenced by videos.
        var bootstrapper = new DatabaseBootstrapper(
            _connectionFactory,
            Repository<VideoSettingRepository>(),
            Repository<VideoRepository>(),
            NullLogger<DatabaseBootstrapper>.Instance);
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        LiveHistoryId = Repository<VideoLiveHistoryRepository>().Insert(new VideoLiveHistoryEntity
        {
            FolderOfVideoToStream = Directory,
            LocalDateTimeStartLive = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local),
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "key",
            PlatformStreamName = "channel",
            UserName = "orbis"
        });
    }

    public long LiveHistoryId { get; }

    public string Directory { get; }

    public string DatabasePath { get; }

    public SqliteConnectionFactory ConnectionFactory => _connectionFactory;

    public T Repository<T>() where T : class
    {
        var type = typeof(T);
        var constructor = type.GetConstructors().Single()
            .GetParameters().Single().ParameterType;
        return (T)Activator.CreateInstance(type, _connectionFactory)!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // The temporary folder is left behind on purpose, it lives in the OS temp directory.
        }
    }
}

public sealed class DatabaseSchemaTests
{
    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using var database = new TemporaryDatabase();

        using var first = database.ConnectionFactory.Open();
        DatabaseSchema.EnsureCreated(first, NullLogger.Instance);
        DatabaseSchema.EnsureCreated(first, NullLogger.Instance);

        using var command = first.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN "
            + "('video', 'setting', 'video_setting', 'audio_setting', 'video_settings_options', 'video_live_history')";
        Assert.Equal(6L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A run that ended with the window still on, or with the machine going down, leaves its video
    /// rows marked LIVE. Nothing is streaming while the application opens, so the rows are released
    /// at startup: left alone, the grid shows a live that stopped weeks ago next to the one that is
    /// really on air, and a start on that channel is refused over it.
    /// </summary>
    [Fact]
    public async Task StartAsync_ReleasesTheLivesLeftRunning()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        videos.Insert(new VideoEntity
        {
            Name = "left-running.mp4",
            VideoPath = "/clips/left-running.mp4",
            Extension = "mp4",
            LiveStatus = LiveStatus.Live,
            ShouldBeStop = true,
            StartDateLive = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local),
            ChannelName = "channel",
            Message = "Video live started: /clips/left-running.mp4",
            VideoSettingId = 1
        });

        await new DatabaseBootstrapper(
            database.ConnectionFactory,
            database.Repository<VideoSettingRepository>(),
            videos,
            NullLogger<DatabaseBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);

        var released = Assert.Single(videos.FindByLiveStatus(LiveStatus.Stopped));
        Assert.Equal("left-running.mp4", released.Name);
        Assert.False(released.ShouldBeStop);

        // What it said about itself no longer holds: it did not stop, it was interrupted.
        Assert.Null(released.Message);
        Assert.Empty(videos.FindByLiveStatus(LiveStatus.Live));
    }

    [Fact]
    public void EnsureCreated_KeepsExistingData()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();
        settings.Insert(new SettingEntity
        {
            Id = 1,
            StreamUrl = "rtmp://a",
            StreamKey = "k",
            ChannelName = "channel",
            IsActive = true
        });

        using (var connection = database.ConnectionFactory.Open())
        {
            DatabaseSchema.EnsureCreated(connection, NullLogger.Instance);
        }

        Assert.Single(settings.FindAll(new Dictionary<string, string>()));
    }

    /// <summary>
    /// Every YouTube ingest the earlier versions offered moves to the one YouTube takes over TLS,
    /// in the configurations and in the lives, except for a key already saved on the new one. A
    /// plain RTMP url to YouTube is one of them: the connection can be answered and the broadcast
    /// still never start, which is a live that reads as connected and is not there.
    /// </summary>
    [Theory]
    [InlineData("rtmps://a.rtmp.youtube.com/live2")]
    [InlineData("rtmp://a.rtmp.youtube.com/live2")]
    public void EnsureCreated_MovesTheOldYouTubeIngest(string old)
    {
        const string New = "rtmps://a.rtmps.youtube.com/live2";

        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();
        settings.Insert(new SettingEntity { Id = 1, StreamUrl = old, StreamKey = "moved", ChannelName = "channel" });
        settings.Insert(new SettingEntity { Id = 2, StreamUrl = old, StreamKey = "twin", ChannelName = "channel" });
        settings.Insert(new SettingEntity { Id = 3, StreamUrl = New, StreamKey = "twin", ChannelName = "channel" });
        settings.Insert(new SettingEntity { Id = 4, StreamUrl = "rtmp://live.twitch.tv/app", StreamKey = "moved", ChannelName = "channel" });

        using var connection = database.ConnectionFactory.Open();
        Execute(connection, $"UPDATE video_live_history SET stream_url = '{old}'");

        DatabaseSchema.EnsureCreated(connection, NullLogger.Instance);

        string UrlOf(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
        }

        Assert.Equal(New, UrlOf("SELECT stream_url FROM setting WHERE id = 1"));
        Assert.Equal(old, UrlOf("SELECT stream_url FROM setting WHERE id = 2"));
        Assert.Equal("rtmp://live.twitch.tv/app", UrlOf("SELECT stream_url FROM setting WHERE id = 4"));
        Assert.Equal(New, UrlOf($"SELECT stream_url FROM video_live_history WHERE pkid = {database.LiveHistoryId}"));
    }

    /// <summary>
    /// A database written by 1.0.15 has no source columns and no scene tables. The migration has
    /// to add them without touching the rows the previous version wrote, because those rows are
    /// the lives the user is watching.
    /// </summary>
    [Fact]
    public void EnsureCreated_MigratesAPreviousDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orbis-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "stream.db");

        try
        {
            using (var legacy = new SqliteConnection($"Data Source={path}"))
            {
                legacy.Open();
                Execute(legacy,
                    """
                    CREATE TABLE video (
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
                        video_setting_id INTEGER
                    );
                    """);
                Execute(legacy,
                    """
                    CREATE TABLE setting (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        stream_url VARCHAR(512) NOT NULL,
                        stream_key VARCHAR(512) NOT NULL,
                        platform_stream_name VARCHAR(255),
                        description TEXT,
                        video_folder VARCHAR(255) DEFAULT '/' NOT NULL,
                        is_active BOOLEAN DEFAULT 'false',
                        channel_name TEXT DEFAULT 'Zingy' NOT NULL
                    );
                    """);
                Execute(legacy,
                    "INSERT INTO video (name, video_path, extension, live_status) VALUES ('clip.mp4', '/videos/clip.mp4', 'mp4', 0);");
            }

            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys = ON;");
            DatabaseSchema.EnsureCreated(connection, NullLogger.Instance);

            // A row that predates the feature still reads back as what it always was: a file.
            var factory = new SqliteConnectionFactory(path);
            var videos = new VideoRepository(factory).FindByScenePkid(0);
            Assert.Empty(videos);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_kind, source_target, audio_enabled FROM video WHERE name = 'clip.mp4';";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.True(reader.IsDBNull(1));
            Assert.False(reader.GetBoolean(2));

            foreach (var table in new[] { "stream_scene", "stream_scene_item" })
            {
                using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
                exists.Parameters.AddWithValue("@name", table);
                Assert.Equal(1L, Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }

            using var sceneColumn = connection.CreateCommand();
            sceneColumn.CommandText = "PRAGMA table_info(setting);";
            using var columns = sceneColumn.ExecuteReader();
            var names = new List<string>();
            while (columns.Read())
            {
                names.Add(columns.GetString(1));
            }

            Assert.Contains("scene_pkid", names);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A file the pool still holds open is not a test failure.
            }
        }
    }

    /// <summary>
    /// Before layouts, one scene was both the layout and the scene of the live started from it.
    /// The one no live used becomes a layout; the one a live went on air with stays that live's,
    /// untouched, and leaves a copy as a layout. A layout keeps rectangles only, so the sources go
    /// and the microphone, which has no rectangle, goes with them.
    /// </summary>
    [Fact]
    public void EnsureCreated_TurnsTheOldScenesIntoLayouts()
    {
        using var database = new TemporaryDatabase();
        using (var legacy = new SqliteConnection($"Data Source={database.DatabasePath}"))
        {
            legacy.Open();
            Execute(legacy, "ALTER TABLE stream_scene DROP COLUMN is_layout;");
            Execute(legacy,
                """
                INSERT INTO stream_scene (pkid, name, width, height) VALUES (1, 'Never started', 1920, 1080), (2, 'On air', 1920, 1080);
                INSERT INTO stream_scene_item (scene_pkid, source_kind, source_target, label, x, y, width, height, audio_enabled) VALUES
                    (1, 1, 'desktop', 'Desktop', 0, 0, 1920, 1080, 0),
                    (2, 0, 'C:/Video/intro.mp4', 'Intro', 0, 0, 1920, 1080, 1),
                    (2, 2, 'video=Cam', 'Webcam', 1440, 20, 460, 260, 0),
                    (2, 3, 'audio=Mic', 'Mic', 0, 0, 0, 0, 1);
                INSERT INTO video (name, video_path, extension, scene_pkid, source_kind) VALUES ('Intro', 'C:/Video/intro.mp4', 'mp4', 2, 0);
                """);

            DatabaseSchema.EnsureCreated(legacy, NullLogger.Instance);
        }

        var scenes = database.Repository<SceneRepository>();
        var layouts = scenes.FindLayouts();
        Assert.Equal(["Never started", "On air"], layouts.Select(layout => layout.Name));
        Assert.Equal(1L, layouts[0].Pkid);
        Assert.NotEqual(2L, layouts[1].Pkid);
        Assert.All(layouts.SelectMany(layout => layout.Items), slot => Assert.Equal(string.Empty, slot.SourceTarget));
        Assert.Equal([(0, 0, 1920, 1080), (1440, 20, 460, 260)], layouts[1].Items.Select(slot => (slot.X, slot.Y, slot.Width, slot.Height)));

        var onAir = scenes.FindByPkid(2);
        Assert.NotNull(onAir);
        Assert.False(onAir!.IsLayout);
        Assert.Equal(["C:/Video/intro.mp4", "video=Cam", "audio=Mic"], onAir.Items.Select(item => item.SourceTarget));
        Assert.True(scenes.IsOnAir(2));
    }

    private static void Execute(SqliteConnection connection, string statement)
    {
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.ExecuteNonQuery();
    }
}

public sealed class SceneRepositoryTests
{
    [Fact]
    public void FindLayouts_LeavesTheScenesOfTheLivesOut()
    {
        using var database = new TemporaryDatabase();
        var scenes = database.Repository<SceneRepository>();

        var layout = scenes.Insert(new SceneEntity
        {
            Name = "Full + corner",
            IsLayout = true,
            Width = 1920,
            Height = 1080,
            Items =
            [
                new SceneItemEntity { X = 0, Y = 0, Width = 1920, Height = 1080 },
                new SceneItemEntity { X = 1440, Y = 20, Width = 460, Height = 260 }
            ]
        });
        scenes.Insert(new SceneEntity
        {
            Name = "Tonight",
            Width = 1920,
            Height = 1080,
            Items = [new SceneItemEntity { SourceKind = SourceKind.Screen, SourceTarget = "desktop", Width = 1920, Height = 1080 }]
        });

        var layouts = scenes.FindLayouts();
        Assert.Single(layouts);
        Assert.Equal(layout, layouts[0].Pkid);
        Assert.True(layouts[0].IsLayout);
        Assert.Equal(2, layouts[0].Items.Count);
        Assert.Equal(2, scenes.FindAll().Count);
        Assert.False(scenes.IsOnAir(layout));
    }

    [Fact]
    public void Insert_ThenUpdate_ReplacesTheItemsInOrder()
    {
        using var database = new TemporaryDatabase();
        var scenes = database.Repository<SceneRepository>();

        var pkid = scenes.Insert(new SceneEntity
        {
            Name = "Intro",
            Description = "desktop with the camera on top",
            Width = 1920,
            Height = 1080,
            LastModified = new DateTime(2026, 3, 1, 9, 0, 0),
            Items =
            [
                new SceneItemEntity { SourceKind = SourceKind.Screen, SourceTarget = "desktop", X = 0, Y = 0, Width = 1920, Height = 1080 },
                new SceneItemEntity { SourceKind = SourceKind.Camera, SourceTarget = "Integrated Camera", Label = "Webcam", X = 1400, Y = 700, Width = 480, Height = 270, AudioEnabled = true }
            ]
        });

        var scene = scenes.FindByPkid(pkid);
        Assert.NotNull(scene);
        Assert.Equal(1920, scene!.Width);
        Assert.Equal(2, scene.Items.Count);
        Assert.Equal(SourceKind.Screen, scene.Items[0].SourceKind);
        Assert.Equal(SourceKind.Camera, scene.Items[1].SourceKind);
        Assert.True(scene.Items[1].AudioEnabled);

        // Dropping the camera has to leave one item, not two with a flag on one of them.
        scene.Items.RemoveAt(1);
        scenes.Update(scene);

        var after = scenes.FindByPkid(pkid);
        Assert.NotNull(after);
        Assert.Single(after!.Items);
        Assert.Equal(SourceKind.Screen, after.Items[0].SourceKind);
    }

    [Fact]
    public void Delete_TakesTheItemsWithIt()
    {
        using var database = new TemporaryDatabase();
        var scenes = database.Repository<SceneRepository>();

        var pkid = scenes.Insert(new SceneEntity
        {
            Name = "Temp",
            Items = [new SceneItemEntity { SourceKind = SourceKind.Camera, SourceTarget = "Cam", Width = 640, Height = 480 }]
        });

        scenes.Delete(pkid);

        Assert.Null(scenes.FindByPkid(pkid));
        Assert.Empty(scenes.FindItemsOf(pkid));
    }

    [Fact]
    public void FindAll_ReturnsTheItemsInStackingOrder()
    {
        using var database = new TemporaryDatabase();
        var scenes = database.Repository<SceneRepository>();

        var first = scenes.Insert(new SceneEntity { Name = "A", Items = [new SceneItemEntity { SourceTarget = "desktop" }] });
        scenes.Insert(new SceneEntity
        {
            Name = "B",
            Items =
            [
                new SceneItemEntity { SourceTarget = "desktop" },
                new SceneItemEntity { SourceTarget = "cam" }
            ]
        });

        var all = scenes.FindAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("A", all[0].Name);
        Assert.Single(all[0].Items);
        Assert.Equal(2, all[1].Items.Count);
        Assert.Equal("desktop", all[1].Items[0].SourceTarget);
        Assert.Equal("cam", all[1].Items[1].SourceTarget);
        Assert.True(first > 0);
    }
}

public sealed class VideoRepositoryTests
{
    private static VideoEntity Video(int pkid, LiveStatus status, string name, long historyId) => new()
    {
        Pkid = pkid,
        Name = name,
        VideoPath = $"/videos/{name}.mp4",
        Extension = "mp4",
        VideoLiveHistoryId = historyId,
        LiveStatus = status,
        ShouldBeStop = false,
        StartDateLive = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local),
        ChannelName = "channel",
        VideoSettingId = TwitchVideoSettingId
    };

    private const int TwitchVideoSettingId = 1;

    [Fact]
    public void InsertAndFind_RoundTripsEveryColumn()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();

        var inserted = videos.Insert(Video(1, LiveStatus.Live, "clip", database.LiveHistoryId));

        var loaded = videos.FindByPkid(inserted);

        Assert.NotNull(loaded);
        Assert.Equal("clip", loaded!.Name);
        Assert.Equal("/videos/clip.mp4", loaded.VideoPath);
        Assert.Equal("mp4", loaded.Extension);
        Assert.Equal(LiveStatus.Live, loaded.LiveStatus);
        Assert.Equal(database.LiveHistoryId, loaded.VideoLiveHistoryId);
        Assert.Equal("channel", loaded.ChannelName);
        Assert.Equal(1, loaded.VideoSettingId);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local), loaded.StartDateLive!.Value);
    }

    [Fact]
    public void InsertAndFind_KeepsTheTileAndTheResolutionOfTheFile()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();

        var video = Video(1, LiveStatus.Offline, "clip", database.LiveHistoryId);
        video.X = 10;
        video.Y = 20;
        video.Width = 640;
        video.Height = 360;
        video.SourceWidth = 1280;
        video.SourceHeight = 720;

        var loaded = videos.FindByPkid(videos.Insert(video))!;

        Assert.Equal((10, 20, 640, 360), (loaded.X, loaded.Y, loaded.Width, loaded.Height));
        Assert.Equal((1280, 720), (loaded.SourceWidth, loaded.SourceHeight));
    }

    [Fact]
    public void FindPaged_AppliesFiltersSortingAndPaging()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        videos.Insert(Video(1, LiveStatus.Live, "a", database.LiveHistoryId));
        videos.Insert(Video(2, LiveStatus.Live, "b", database.LiveHistoryId));
        videos.Insert(Video(3, LiveStatus.Stopped, "c", database.LiveHistoryId));

        var page = videos.FindPaged(
            new Dictionary<string, string> { ["liveStatus"] = "LIVE" },
            new PageRequest(0, 10, [new SortOrder("name", false)]));

        Assert.Equal(2, page.TotalElements);
        Assert.Equal(1, page.TotalPages);
        Assert.Equal(["a", "b"], page.Items.Select(video => video.Name));

        var secondPage = videos.FindPaged(
            new Dictionary<string, string> { ["liveStatus"] = "LIVE" },
            new PageRequest(1, 1, [new SortOrder("name", false)]));

        Assert.Equal(2, secondPage.TotalElements);
        Assert.Equal(2, secondPage.TotalPages);
        Assert.Equal("b", Assert.Single(secondPage.Items).Name);
    }

    [Fact]
    public void FindPaged_SupportsTheVideoLiveHistoryJoin()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        var otherHistory = database.Repository<VideoLiveHistoryRepository>().Insert(new VideoLiveHistoryEntity
        {
            FolderOfVideoToStream = database.Directory,
            LocalDateTimeStartLive = new DateTime(2026, 2, 2, 10, 0, 0, DateTimeKind.Local),
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "key",
            PlatformStreamName = "channel"
        });
        videos.Insert(Video(1, LiveStatus.Stopped, "a", database.LiveHistoryId));
        videos.Insert(Video(2, LiveStatus.Stopped, "b", otherHistory));

        var page = videos.FindPaged(
            new Dictionary<string, string> { ["videoLiveHistory"] = otherHistory.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            PageRequest.Default(10, "pkid", false));

        Assert.Equal("b", Assert.Single(page.Items).Name);
    }

    [Fact]
    public void SetStopFlag_UpdatesThePollingFlagOfTheStreamer()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        videos.Insert(Video(1, LiveStatus.Live, "a", database.LiveHistoryId));

        videos.SetStopFlag(1, true);

        Assert.True(videos.FindByPkid(1)!.ShouldBeStop);
    }

    [Fact]
    public void SetVideoSetting_UpdatesOnlyTheConfigurationReference()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        videos.Insert(Video(1, LiveStatus.Live, "a", database.LiveHistoryId));

        var videoSettings = database.Repository<VideoSettingRepository>();
        var twitch = videoSettings.FindById(TwitchVideoSettingId)!;
        var youtube = videoSettings.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Youtube").Single();

        videos.SetVideoSetting(1, youtube.Id);

        var loaded = videos.FindByPkid(1)!;
        Assert.Equal(youtube.Id, loaded.VideoSettingId);
        Assert.NotEqual(twitch.Id, loaded.VideoSettingId);
        Assert.Equal("a", loaded.Name);
    }

    [Fact]
    public void FindByLiveStatusAndChannelName_CombinesBothConditions()
    {
        using var database = new TemporaryDatabase();
        var videos = database.Repository<VideoRepository>();
        videos.Insert(Video(1, LiveStatus.Live, "a", database.LiveHistoryId));
        videos.Insert(Video(2, LiveStatus.Stopped, "b", database.LiveHistoryId));

        var found = videos.FindByLiveStatusAndChannelName(LiveStatus.Live, "channel");

        Assert.Equal("a", Assert.Single(found).Name);
    }
}

public sealed class SettingRepositoryTests
{
    [Fact]
    public void Insert_GeneratesTheNextIdentifier()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();

        var first = settings.Insert(new SettingEntity
        {
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "first",
            ChannelName = "one",
            IsActive = true
        });
        var second = settings.Insert(new SettingEntity
        {
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "second",
            ChannelName = "two",
            IsActive = false
        });

        Assert.Equal(1, first);
        Assert.Equal(2, second);

    }

    [Fact]
    public void Update_ReplacesEveryField()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();
        var id = settings.Insert(new SettingEntity
        {
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "key",
            ChannelName = "channel",
            Description = "old",
            IsActive = true,
            VideoFolder = "/videos"
        });

        settings.Update(new SettingEntity
        {
            Id = id,
            StreamUrl = "rtmp://ingest/live",
            StreamKey = "key",
            ChannelName = "channel",
            Description = "new",
            IsActive = false,
            VideoFolder = "/other"
        });

        var loaded = settings.FindById(id)!;
        Assert.Equal("new", loaded.Description);
        Assert.False(loaded.IsActive);
        Assert.Equal("/other", loaded.VideoFolder);
    }

    [Fact]
    public void FindAll_SupportsDynamicFilters()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();
        settings.Insert(new SettingEntity { StreamUrl = "rtmp://a", StreamKey = "1", ChannelName = "one", IsActive = true });
        settings.Insert(new SettingEntity { StreamUrl = "rtmp://b", StreamKey = "2", ChannelName = "two", IsActive = true });

        var found = settings.FindAll(new Dictionary<string, string> { ["streamUrl"] = "rtmp://b" });

        Assert.Equal("two", Assert.Single(found).ChannelName);
    }

    [Fact]
    public void Delete_RemovesTheRow()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<SettingRepository>();
        var id = settings.Insert(new SettingEntity { StreamUrl = "rtmp://a", StreamKey = "1", ChannelName = "one" });

        settings.Delete(id);

        Assert.Null(settings.FindById(id));
    }
}
