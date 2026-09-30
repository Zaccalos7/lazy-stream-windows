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

        services.AddSingleton<DatabaseBootstrapper>();
        services.AddHostedService(provider => provider.GetRequiredService<DatabaseBootstrapper>());

        return services;
    }
}
