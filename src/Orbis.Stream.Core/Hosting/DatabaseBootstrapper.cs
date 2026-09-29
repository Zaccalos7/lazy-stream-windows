using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Port of the startup sequence of the Java application: create/upgrade the SQLite schema, then
/// the <c>CommandLineRunner</c> of <c>DatabaseInitializer</c> which keeps exactly one default
/// video and audio setting per platform.
/// </summary>
public sealed class DatabaseBootstrapper : IHostedService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly ILogger<DatabaseBootstrapper> _logger;

    public DatabaseBootstrapper(
        SqliteConnectionFactory connectionFactory,
        VideoSettingRepository videoSettingRepository,
        ILogger<DatabaseBootstrapper> logger)
    {
        _connectionFactory = connectionFactory;
        _videoSettingRepository = videoSettingRepository;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using (var connection = _connectionFactory.Open())
        {
            DatabaseSchema.EnsureCreated(connection, _logger);
        }

        AddDefaultVideoAndAudioSetting();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void AddDefaultVideoAndAudioSetting()
    {
        _logger.LogInformation("Initializing default VideoSetting...");
        InitializeDefaultVideoSetting("Twitch");
        InitializeDefaultVideoSetting("Youtube");
    }

    /// <summary>
    /// The default settings are named after the platform they are for: in the wizard and in the
    /// settings two rows both called "test" are impossible to tell apart, and the name is the first
    /// thing the user reads there.
    /// </summary>
    private static string TitleOf(string platform) => "Default " + platform;

    /// <summary>
    /// The name the first versions gave to every default setting. An installation that already has
    /// one keeps it: only the placeholder is renamed, so a name the user chose is never overwritten.
    /// </summary>
    private static bool IsPlaceholderTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), "test", StringComparison.OrdinalIgnoreCase);

    private void InitializeDefaultVideoSetting(string platform)
    {
        var existing = _videoSettingRepository.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration(platform);
        if (existing.Count > 0)
        {
            // The first of the duplicates stays, the others go, exactly as before: a platform with
            // two default settings would offer the wizard two identical rows.
            foreach (var duplicate in existing.Skip(1))
            {
                _videoSettingRepository.Delete(duplicate.Id!.Value);
            }

            var kept = existing[0];
            if (IsPlaceholderTitle(kept.Title))
            {
                kept.Title = TitleOf(platform);
                kept.LastModified = DateTime.Now;
                _videoSettingRepository.Update(kept);
                _logger.LogInformation(
                    "Default VideoSetting of {Platform} renamed to {Title}", platform, kept.Title);
            }

            return;
        }

        var setting = new VideoSettingEntity
        {
            Title = TitleOf(platform),
            VideoCodec = 27,
            VideoCodecName = "libx264",
            PixelFormat = 0,
            VideoBitrate = 5_000_000,
            LastModified = null,
            IsVideoAndAudioSettingActive = true,
            GopSize = 2,
            VideoSettingsOptions =
            [
                new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
                new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" }
            ],
            VideoFormat = "flv",
            AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
            IsDefaultConfiguration = true,
            DefaultPlatformConfiguration = platform
        };

        _videoSettingRepository.Insert(setting);
        _logger.LogInformation("Default VideoSetting initialized for {Platform}", platform);
    }
}
