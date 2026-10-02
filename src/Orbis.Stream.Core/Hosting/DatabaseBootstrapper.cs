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
        InitializeDefaultVideoSetting("Twitch", createHighQuality: true);
        InitializeDefaultVideoSetting("Twitch", createHighQuality: false);
        InitializeDefaultVideoSetting("Youtube", createHighQuality: true);
        InitializeDefaultVideoSetting("Youtube", createHighQuality: false);
    }

    /// <summary>
    /// The default settings are named after the platform they are for: in the wizard and in the
    /// settings two rows both called "test" are impossible to tell apart, and the name is the first
    /// thing the user reads there.
    /// </summary>
    private static string TitleOf(string platform) => "Default " + platform;

    private static string LowTitleOf(string platform) => "Default Low " + platform;

    /// <summary>
    /// The name the first versions gave to every default setting. An installation that already has
    /// one keeps it: only the placeholder is renamed, so a name the user chose is never overwritten.
    /// </summary>
    private static bool IsPlaceholderTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), "test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the platform already has its default setting, which is found by the flag and not by
    /// its title: the title is the one thing the user may have changed, and an installation from the
    /// first versions still calls it "test". Looked up by title, both were missed and a second default
    /// was added next to them on every upgrade. The first of the duplicates stays, the others go: a
    /// platform with two default settings would offer the wizard two identical rows.
    /// </summary>
    private bool KeepTheDefaultOf(string platform, string title)
    {
        var existing = _videoSettingRepository.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration(platform);
        if (existing.Count == 0)
        {
            return false;
        }

        foreach (var duplicate in existing.Skip(1))
        {
            _videoSettingRepository.Delete(duplicate.Id!.Value);
        }

        var kept = existing[0];
        if (IsPlaceholderTitle(kept.Title))
        {
            kept.Title = title;
            kept.LastModified = DateTime.Now;
            _videoSettingRepository.Update(kept);
            _logger.LogInformation("Default VideoSetting of {Platform} renamed to {Title}", platform, title);
        }

        return true;
    }

    private void InitializeDefaultVideoSetting(string platform, bool createHighQuality)
    {
        var title = createHighQuality ? TitleOf(platform) : LowTitleOf(platform);

        if (createHighQuality ? KeepTheDefaultOf(platform, title) : _videoSettingRepository.FindByTitleAndPlatform(title, platform) is not null)
        {
            return;
        }

        if (createHighQuality)
        {
            // High quality defaults (original)
            var (bitrate, audioBitrate, videoFormat, gopSize, extraOptions) = platform switch
            {
                "Twitch" => (6_000_000, 160_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "veryfast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" },
                    new VideoSettingsOptionEntity { Key = "profile", Value = "high" },
                    new VideoSettingsOptionEntity { Key = "x264-params", Value = "rc_lookahead=20" }
                }),
                "Youtube" => (8_000_000, 192_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "veryfast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" },
                    new VideoSettingsOptionEntity { Key = "profile", Value = "high" },
                    new VideoSettingsOptionEntity { Key = "x264-params", Value = "rc_lookahead=20" }
                }),
                _ => (5_000_000, 128_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "veryfast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" }
                })
            };

            var setting = new VideoSettingEntity
            {
                Title = title,
                VideoCodec = 27,
                VideoCodecName = "libx264",
                PixelFormat = 0,
                VideoBitrate = bitrate,
                VideoWidth = null,      // Keep source resolution
                VideoHeight = null,
                FrameRate = null,       // Keep source frame rate
                LastModified = null,
                IsVideoAndAudioSettingActive = true,
                GopSize = gopSize,
                VideoSettingsOptions = extraOptions.ToList(),
                VideoFormat = videoFormat,
                AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = audioBitrate },
                IsDefaultConfiguration = true,
                DefaultPlatformConfiguration = platform
            };

            _videoSettingRepository.Insert(setting);
            _logger.LogInformation("High quality VideoSetting initialized for {Platform}: {Title}", platform, title);
        }
        else
        {
            // Low CPU defaults (new) - aggressive bitrate reduction for low-end hardware
            var (bitrate, audioBitrate, videoFormat, gopSize, extraOptions) = platform switch
            {
                "Twitch" => (3_000_000, 96_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" },
                    new VideoSettingsOptionEntity { Key = "profile", Value = "main" },
                    new VideoSettingsOptionEntity { Key = "x264-params", Value = "scenecut=0:rc_lookahead=0" }
                }),
                "Youtube" => (2_500_000, 96_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" },
                    new VideoSettingsOptionEntity { Key = "profile", Value = "main" },
                    new VideoSettingsOptionEntity { Key = "x264-params", Value = "scenecut=0:rc_lookahead=0" }
                }),
                _ => (2_000_000, 64_000, "flv", 2, new[]
                {
                    new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
                    new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" }
                })
            };

            var setting = new VideoSettingEntity
            {
                Title = title,
                VideoCodec = 27,
                VideoCodecName = "libx264",
                PixelFormat = 0,
                VideoBitrate = bitrate,
                VideoWidth = 1280,
                VideoHeight = 720,
                FrameRate = 30,
                LastModified = null,
                IsVideoAndAudioSettingActive = true,
                GopSize = gopSize,
                VideoSettingsOptions = extraOptions.ToList(),
                VideoFormat = videoFormat,
                AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = audioBitrate },
                IsDefaultConfiguration = false,  // Not the primary default
                DefaultPlatformConfiguration = platform
            };

            _videoSettingRepository.Insert(setting);
            _logger.LogInformation("Low CPU VideoSetting initialized for {Platform}: {Title}", platform, title);
        }
    }
}
