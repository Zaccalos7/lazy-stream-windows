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
        services.AddSingleton<FfmpegVideoPlaylistStreamer>();
        services.AddSingleton<IVideoPlaylistStreamer>(provider => (IVideoPlaylistStreamer)provider.GetRequiredService<FfmpegVideoPlaylistStreamer>());

        services.AddSingleton(provider => new BackgroundTaskExecutor(
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackgroundTaskExecutor>>()));
        services.AddSingleton<StreamingService>();
        services.AddSingleton<VideoService>();
        services.AddSingleton<SettingService>();
        services.AddSingleton<VideoSettingService>();
        services.AddSingleton<ImageService>();
        services.AddSingleton<LivePreviewService>();
        // What the canvas can be built from. The folders are read on every listing, so a folder
        // added in the channel settings shows its files without restarting the application.
        services.AddSingleton<ISourceProvider, DisplaySourceProvider>();
        services.AddSingleton<ISourceProvider, CameraSourceProvider>();
        services.AddSingleton<ISourceProvider>(provider =>
            new FileSourceProvider(ConfiguredFolders(provider.GetRequiredService<SettingRepository>())));
        services.AddSingleton<SourceCatalogService>();
        services.AddSingleton<SourceSnapshotService>();
        services.AddSingleton<SceneService>();

        services.AddSingleton<DatabaseBootstrapper>();
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseBootstrapper>());

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
