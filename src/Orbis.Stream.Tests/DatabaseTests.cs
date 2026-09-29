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
