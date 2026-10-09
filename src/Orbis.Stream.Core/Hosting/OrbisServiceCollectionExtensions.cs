using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
using Orbis.Stream.Core.SystemInfo;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Composition root of the application: the same singletons the Spring configuration declared,
/// plus the SQLite schema bootstrap and the default video settings of the Java version.
/// </summary>
public static class OrbisServiceCollectionExtensions
{
    /// <summary>The name the client that reads the pages of the streaming platforms is built under.</summary>
    private const string PlatformPlayerClient = "platform-player";

    public static IServiceCollection AddOrbisStream(
        this IServiceCollection services,
        OrbisRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.EnsureDirectories();

        services.AddHttpContextAccessor();
        services.AddSingleton(options);

        services.AddSingleton(_ => new SqliteConnectionFactory(options.DatabasePath));
        services.AddSingleton<VideoRepository>();
        services.AddSingleton<VideoSettingRepository>();
        services.AddSingleton<SettingRepository>();
        services.AddSingleton<VideoLiveHistoryRepository>();
        services.AddSingleton<SceneRepository>();
        services.AddSingleton<SceneButtonRepository>();

        var messagesDirectory = MessageCatalog.LocateMessagesDirectory(contentRoot: options.DataDirectory)
            ?? Path.Combine(AppContext.BaseDirectory, "Messages");
        services.AddSingleton<MessageCatalog>(provider => new MessageCatalog(
            messagesDirectory,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MessageCatalog>>()));
        services.AddSingleton<Localizer>();
        services.AddSingleton<ResponseFactory>();

        services.AddSingleton<ISystemInfoProvider>(_ => SystemInfoProviderFactory.CreateDefault());
        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<LiveChangeNotifier>();

        services.AddSingleton(_ => new FfmpegToolLocator(options.FfmpegPath, options.FfprobePath));
        services.AddSingleton<FfmpegProbe>();
        services.AddSingleton<StreamingSessionRegistry>();
        services.AddSingleton<LivePreviewFrames>();
        // What the scene deck and the spots put on air in place of the program of each live.
        services.AddSingleton<LiveTakeovers>();
        services.AddSingleton(provider => new MediaProxyService(
            options,
            provider.GetRequiredService<FfmpegToolLocator>(),
            provider.GetRequiredService<FfmpegProbe>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MediaProxyService>>()));
        services.AddSingleton<EncoderTuningService>();
        services.AddSingleton<FfmpegVideoPlaylistStreamer>();
        services.AddSingleton<IVideoPlaylistStreamer>(provider => (IVideoPlaylistStreamer)provider.GetRequiredService<FfmpegVideoPlaylistStreamer>());

        services.AddSingleton(provider => new BackgroundTaskExecutor(
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackgroundTaskExecutor>>()));
        services.AddSingleton<StreamingService>();
        services.AddSingleton<LiveHistoryCleanupService>();
        services.AddSingleton<VideoService>();
        services.AddSingleton<SettingService>();
        services.AddSingleton<VideoSettingService>();
        services.AddSingleton<ImageService>();
        // The players of the platforms a live goes on. The client is given a name of its own and a
        // short leash: a platform page that does not answer in time must not hold the preview open
        // waiting for it, and the answers are held in the service so a page asking again does not
        // ask YouTube again.
        services.AddHttpClient(PlatformPlayerClient, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
            // A platform page is the page a browser gets. Without these it answers a page that is
            // not a browser one with a consent wall instead of the channel.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        });
        services.AddSingleton(provider => new LivePlatformEmbeds(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(PlatformPlayerClient),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LivePlatformEmbeds>>()));
        // Whether YouTube shows the lives it is sent: a publish the ingest took is not a broadcast on air.
        services.AddSingleton<YouTubeAirWatch>();
        services.AddHostedService(provider => provider.GetRequiredService<YouTubeAirWatch>());
        services.AddSingleton<LivePreviewService>();
        // What the canvas can be built from. The folders are read on every listing, so a folder
        // added in the channel settings shows its files without restarting the application.
        services.AddSingleton<ISourceProvider, DisplaySourceProvider>();
        services.AddSingleton<ISourceProvider, CameraSourceProvider>();
        services.AddSingleton<ISourceProvider>(provider =>
            new FileSourceProvider(ConfiguredFolders(provider.GetRequiredService<SettingRepository>())));
        services.AddSingleton<SourceCatalogService>();
        services.AddSingleton<SourceSnapshotService>();
        services.AddSingleton<OverlayLibrary>();
        services.AddSingleton<SceneButtonService>();
        services.AddSingleton<SceneService>();

        services.AddSingleton<DatabaseBootstrapper>();
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseBootstrapper>());
        services.AddHostedService<AutoCleanupService>();
        // After the bootstrapper, which is the one that makes sure the settings are there.
        services.AddHostedService<EncoderTuningStartup>();

        return services;
    }

    /// <summary>
    /// An iterator, so it is read again every time it is walked: the providers are singletons and
    /// a list captured when the container was built would never see a folder added afterwards.
    /// </summary>
    private static IEnumerable<string> ConfiguredFolders(SettingRepository settings)
    {
        foreach (var setting in settings.FindAll(new Dictionary<string, string>()))
        {
            if (!string.IsNullOrWhiteSpace(setting.VideoFolder))
            {
                yield return setting.VideoFolder;
            }
        }
    }
}
