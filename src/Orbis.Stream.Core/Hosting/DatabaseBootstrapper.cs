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
    private readonly VideoRepository _videoRepository;
    private readonly ILogger<DatabaseBootstrapper> _logger;

    public DatabaseBootstrapper(
        SqliteConnectionFactory connectionFactory,
        VideoSettingRepository videoSettingRepository,
        VideoRepository videoRepository,
        ILogger<DatabaseBootstrapper> logger)
    {
        _connectionFactory = connectionFactory;
        _videoSettingRepository = videoSettingRepository;
        _videoRepository = videoRepository;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using (var connection = _connectionFactory.Open())
        {
            DatabaseSchema.EnsureCreated(connection, _logger);
        }

        ReleaseLivesLeftRunning();
        AddDefaultVideoAndAudioSetting();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Nothing is streaming while the application is opening, so a video row still marked LIVE was
    /// left behind by a run that never ended: the window was closed with ffmpeg on, or the machine
    /// went down. Left as it is, the live grid and the live history show a live that stopped weeks
    /// ago next to the one that is really on air, and a start on that channel is refused over it.
    /// </summary>
    private void ReleaseLivesLeftRunning()
    {
        foreach (var video in _videoRepository.FindByLiveStatus(LiveStatus.Live))
        {
            _logger.LogInformation("Video {Name} was still marked live at startup and is released", video.Name);

            video.LiveStatus = LiveStatus.Stopped;
            video.ShouldBeStop = false;
            // What it says about itself no longer holds: it did not stop, it was interrupted.
            video.Message = null;
            _videoRepository.Update(video);
        }
    }

    private void AddDefaultVideoAndAudioSetting()
    {
        _logger.LogInformation("Initializing default VideoSetting...");

        var oldLowCpuTwitch = _videoSettingRepository.FindByTitleAndPlatform("Low CPU Default Twitch", "Twitch");
        if (oldLowCpuTwitch != null) _videoSettingRepository.Delete(oldLowCpuTwitch.Id!.Value);

        var oldLowCpuYoutube = _videoSettingRepository.FindByTitleAndPlatform("Low CPU Default Youtube", "Youtube");
        if (oldLowCpuYoutube != null) _videoSettingRepository.Delete(oldLowCpuYoutube.Id!.Value);

        InitializeDefaultVideoSetting("Twitch", createHighQuality: true);
        InitializeDefaultVideoSetting("Twitch", createHighQuality: false);
        InitializeDefaultVideoSetting("Youtube", createHighQuality: true);
        InitializeDefaultVideoSetting("Youtube", createHighQuality: false);
        InitializeDefaultVideoSetting("Kick", createHighQuality: true);
        InitializeDefaultVideoSetting("Kick", createHighQuality: false);
        InitializeDefaultVideoSetting("Facebook Gaming", createHighQuality: true);
        InitializeDefaultVideoSetting("Facebook Gaming", createHighQuality: false);
        InitializeDefaultVideoSetting("TikTok", createHighQuality: true);
        InitializeDefaultVideoSetting("TikTok", createHighQuality: false);
        UpgradeLegacyYouTubeDefaults();
    }

    /// <summary>What a default setting is made of: everything the seed decides, per platform and level.</summary>
    private sealed record DefaultSpec(
        int Bitrate,
        int AudioBitrate,
        int? Width,
        int? Height,
        double? FrameRate,
        (string Key, string Value)[] Options);

    /// <summary>
    /// YouTube, the setting it goes on air with. A fixed 1080p30 at a constant 6 Mbps: YouTube
    /// judges a live against the resolution and the rate it announces, and "the size of the
    /// source" made the live whatever the first file was - a 360p clip, and YouTube asked for
    /// 400 Kbps and reported the 6 Mbps it got as not enough video. No x264-params of its own:
    /// they used to replace the CBR parameters the YouTube profile adds to x264.
    /// </summary>
    private static readonly DefaultSpec YouTubeHigh = new(
        6_000_000, 128_000, 1920, 1080, 30,
        [("preset", "veryfast"), ("profile", "high")]);

    /// <summary>YouTube on a light machine: 720p30 at 3 Mbps, the range YouTube gives that size.</summary>
    private static readonly DefaultSpec YouTubeLow = new(
        3_000_000, 128_000, 1280, 720, 30,
        [("preset", "superfast"), ("profile", "main"), ("x264-params", "scenecut=0:rc_lookahead=0")]);

    /// <summary>
    /// Kick: a fixed 1080p30 at 6 Mbps, under the 8 Mbps its IVS ingest takes with the headroom a
    /// home uplink needs, and the 160 Kbps of sound Kick asks for. The size is named because the
    /// whole live keeps one format there (StreamPlatformProfile.Kick), like on YouTube, and the
    /// first file of a playlist is no reason for all of it to go out small.
    /// </summary>
    private static readonly DefaultSpec KickHigh = new(
        6_000_000, 160_000, 1920, 1080, 30,
        [("preset", "veryfast"), ("profile", "high")]);

    /// <summary>Kick on a light machine: 720p30 at 3 Mbps.</summary>
    private static readonly DefaultSpec KickLow = new(
        3_000_000, 128_000, 1280, 720, 30,
        [("preset", "superfast"), ("profile", "main"), ("x264-params", "scenecut=0:rc_lookahead=0")]);

    /// <summary>
    /// Facebook Gaming: a fixed 1080p30 at 4.5 Mbps, the middle of the 3 to 6 Mbps Facebook takes
    /// for that size and rate - its ingest keeps the settings of a broadcast from changing halfway
    /// and checks them against what it announced, so the size is named - with the 128 Kbps of
    /// sound it asks for, and the main profile OBS sends it.
    /// </summary>
    private static readonly DefaultSpec FacebookGamingHigh = new(
        4_500_000, 128_000, 1920, 1080, 30,
        [("preset", "veryfast"), ("profile", "main")]);

    /// <summary>Facebook Gaming on a light machine: 720p30 at 3 Mbps, under the 4 Mbps of that size.</summary>
    private static readonly DefaultSpec FacebookGamingLow = new(
        3_000_000, 128_000, 1280, 720, 30,
        [("preset", "superfast"), ("profile", "main"), ("x264-params", "scenecut=0:rc_lookahead=0")]);

    /// <summary>
    /// TikTok LIVE: 1080x1920, standing up, at 30 fps and 4.5 Mbps - the middle of what TikTok is
    /// known to take for that size, which it transcodes for phones anyway, so more is upload spent
    /// for nothing. A landscape file is fitted into the frame (StreamPlatformProfile.Portrait).
    /// </summary>
    private static readonly DefaultSpec TikTokHigh = new(
        4_500_000, 128_000, 1080, 1920, 30,
        [("preset", "veryfast"), ("profile", "high")]);

    /// <summary>TikTok on a light machine: 720x1280 at 30 fps and 2.5 Mbps.</summary>
    private static readonly DefaultSpec TikTokLow = new(
        2_500_000, 128_000, 720, 1280, 30,
        [("preset", "superfast"), ("profile", "main"), ("x264-params", "scenecut=0:rc_lookahead=0")]);

    private static DefaultSpec SpecOf(string platform, bool highQuality) => (platform, highQuality) switch
    {
        ("Twitch", true) => new(6_000_000, 160_000, null, null, null,
            [("preset", "veryfast"), ("tune", "zerolatency"), ("profile", "high"), ("x264-params", "rc_lookahead=20")]),
        ("Youtube", true) => YouTubeHigh,
        ("Kick", true) => KickHigh,
        ("Facebook Gaming", true) => FacebookGamingHigh,
        ("TikTok", true) => TikTokHigh,
        (_, true) => new(5_000_000, 128_000, null, null, null,
            [("preset", "veryfast"), ("tune", "zerolatency")]),

        // Low CPU defaults - aggressive bitrate reduction for low-end hardware
        ("Twitch", false) => new(3_000_000, 96_000, 1280, 720, 30,
            [("preset", "ultrafast"), ("tune", "zerolatency"), ("profile", "main"), ("x264-params", "scenecut=0:rc_lookahead=0")]),
        ("Youtube", false) => YouTubeLow,
        ("Kick", false) => KickLow,
        ("Facebook Gaming", false) => FacebookGamingLow,
        ("TikTok", false) => TikTokLow,
        _ => new(2_000_000, 64_000, 1280, 720, 30,
            [("preset", "ultrafast"), ("tune", "zerolatency")])
    };

    /// <summary>
    /// The YouTube defaults the earlier versions seeded - source resolution at 8 Mbps, 720p at
    /// 2.5 Mbps with 96 Kbps of sound - are brought to the current ones. They are recognised by
    /// their numbers and not by their options: EncoderTuningService rewrites the options of the
    /// defaults at every start. A row with other numbers is one the user edited, and stays theirs.
    /// </summary>
    private void UpgradeLegacyYouTubeDefaults()
    {
        foreach (var setting in _videoSettingRepository.FindByIsDefaultConfigurationTrueAndDefaultPlatformConfiguration("Youtube"))
        {
            if (setting is { VideoWidth: null, VideoHeight: null, FrameRate: null, VideoBitrate: 8_000_000 })
            {
                Apply(setting, YouTubeHigh);
            }
        }

        if (_videoSettingRepository.FindByTitleAndPlatform(LowTitleOf("Youtube"), "Youtube") is
            { VideoWidth: 1280, VideoHeight: 720, VideoBitrate: 2_500_000, AudioSetting.AudioBitrate: 96_000 } low)
        {
            Apply(low, YouTubeLow);
        }

        void Apply(VideoSettingEntity setting, DefaultSpec spec)
        {
            Fill(setting, spec);
            setting.LastModified = DateTime.Now;
            _videoSettingRepository.Update(setting);
            _logger.LogInformation("Default VideoSetting {Title} moved to the current YouTube settings", setting.Title);
        }
    }

    /// <summary>Writes what the seed decides onto a setting, new or already there.</summary>
    private static void Fill(VideoSettingEntity setting, DefaultSpec spec)
    {
        setting.VideoCodec = 27;
        setting.VideoCodecName = "libx264";
        setting.PixelFormat = 0;
        setting.VideoBitrate = spec.Bitrate;
        setting.VideoWidth = spec.Width;
        setting.VideoHeight = spec.Height;
        setting.FrameRate = spec.FrameRate;
        setting.GopSize = 2;
        setting.VideoFormat = "flv";
        setting.VideoSettingsOptions = spec.Options
            .Select(option => new VideoSettingsOptionEntity { Key = option.Key, Value = option.Value })
            .ToList();

        // The row of the sound is updated in place when there is one, so its id stays.
        setting.AudioSetting ??= new AudioSettingEntity();
        setting.AudioSetting.AudioCodec = 86018;
        setting.AudioSetting.AudioBitrate = spec.AudioBitrate;
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

        var setting = new VideoSettingEntity
        {
            Title = title,
            LastModified = null,
            IsVideoAndAudioSettingActive = true,
            // The low one is a second choice, not the primary default.
            IsDefaultConfiguration = createHighQuality,
            DefaultPlatformConfiguration = platform
        };
        Fill(setting, SpecOf(platform, createHighQuality));

        _videoSettingRepository.Insert(setting);
        _logger.LogInformation(
            "{Level} VideoSetting initialized for {Platform}: {Title}",
            createHighQuality ? "High quality" : "Low CPU",
            platform,
            title);
    }
}
