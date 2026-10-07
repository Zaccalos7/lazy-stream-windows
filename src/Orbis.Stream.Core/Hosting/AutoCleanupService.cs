using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Background service that runs the auto-cleanup of old live history rows at the configured interval.
/// </summary>
public sealed class AutoCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AutoCleanupService> _logger;

    public AutoCleanupService(IServiceProvider services, ILogger<AutoCleanupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the app start
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto cleanup failed");
            }

            // Wait for the next interval; if disabled, check again in an hour.
            var interval = GetIntervalMinutes();
            await DelayAsync(interval > 0 ? TimeSpan.FromMinutes(interval) : TimeSpan.FromHours(1), stoppingToken);
        }
    }

    /// <summary>
    /// The longest wait <see cref="Task.Delay(TimeSpan, CancellationToken)"/> takes.
    /// </summary>
    internal static readonly TimeSpan LongestDelay = TimeSpan.FromDays(1);

    /// <summary>
    /// A wait of any length. Task.Delay refuses one past about 49 days, and an interval of two
    /// months is past it: the exception left ExecuteAsync and stopped the whole application, live
    /// included. The wait is cut into days instead.
    /// </summary>
    internal static async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        while (duration > TimeSpan.Zero)
        {
            var step = duration < LongestDelay ? duration : LongestDelay;
            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
            duration -= step;
        }
    }

    private int GetIntervalMinutes()
    {
        using var scope = _services.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<SettingService>();
        var settings = settingService.RetrieveSettings(new Dictionary<string, string>());
        var activeConfig = settings.FirstOrDefault(c => c.IsActive == true);

        if (activeConfig?.AutoCleanupEnabled == true && activeConfig.AutoCleanupIntervalMonths > 0)
        {
            return activeConfig.AutoCleanupIntervalMonths * 30 * 24 * 60; // Convert months to minutes
        }

        return 0;
    }

    private async Task RunCleanupAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<SettingService>();
        var videoService = scope.ServiceProvider.GetRequiredService<VideoService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AutoCleanupService>>();

        var settings = settingService.RetrieveSettings(new Dictionary<string, string>());
        var activeConfig = settings.FirstOrDefault(c => c.IsActive == true);

        // "Older than yesterday" (zero) is a period like any other and so runs, and so does
        // "older than now" (minus one), which takes everything: only a period below that is a
        // refused parameter.
        if (activeConfig?.AutoCleanupEnabled != true || activeConfig.AutoCleanupOlderThanMonths < LiveHistoryCleanupService.Everything)
        {
            return;
        }

        try
        {
            var result = videoService.DeleteOldLiveHistory(activeConfig.AutoCleanupOlderThanMonths);
            logger.LogInformation("Auto cleanup: {Message}", result.Body.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto cleanup failed");
        }
    }
}