using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Pages;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class EncoderQualityTests
{
    private static readonly MediaProbeResult Probe = new(1920, 1080, 30, true, 2, 60);

    private static VideoSettingEntity Setting(string encoder, params (string Key, string Value)[] options) => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = encoder,
        PixelFormat = 0,
        VideoBitrate = 6_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
        VideoSettingsOptions = [.. options.Select(option => new VideoSettingsOptionEntity { Key = option.Key, Value = option.Value })]
    };

    private static string Command(VideoSettingEntity setting, EncoderQuality? quality = null) =>
        string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://ingest/live/key", Probe, setting, Quality: quality)));

    [Theory]
    [InlineData("libx264", EncoderQuality.Light, "-preset superfast")]
    [InlineData("libx264", EncoderQuality.Balanced, "-preset veryfast")]
    [InlineData("libx264", EncoderQuality.High, "-preset faster")]
    [InlineData("h264_nvenc", EncoderQuality.Light, "-preset p1")]
    [InlineData("h264_nvenc", EncoderQuality.Balanced, "-preset p4")]
    [InlineData("h264_nvenc", EncoderQuality.High, "-preset p6")]
    [InlineData("h264_qsv", EncoderQuality.Light, "-preset veryfast")]
    [InlineData("h264_qsv", EncoderQuality.Balanced, "-preset medium")]
    [InlineData("h264_amf", EncoderQuality.Light, "-quality speed")]
    [InlineData("h264_amf", EncoderQuality.High, "-quality quality")]
    public void EveryEncoderSpellsTheLevelItsOwnWay(string encoder, EncoderQuality quality, string expected)
    {
        var command = Command(Setting(encoder, (VideoSettingQuality.OptionKey, quality.ToString())));

        Assert.Contains(expected, command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMeasuredLevelOverridesTheAutomaticOne()
    {
        var setting = Setting("h264_nvenc", (VideoSettingQuality.OptionKey, "Auto"));

        Assert.Contains("-preset p1", Command(setting, EncoderQuality.Light), StringComparison.Ordinal);

        // Nothing measured yet: automatic is the balanced level.
        Assert.Contains("-preset p4", Command(setting), StringComparison.Ordinal);
    }

    [Fact]
    public void APresetTypedByHandWinsOverTheLevel()
    {
        var command = Command(Setting("h264_nvenc", ("preset", "p7"), (VideoSettingQuality.OptionKey, "Light")));

        Assert.Contains("-preset p7", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-preset p1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecisionsOfTheApplicationNeverReachFfmpeg()
    {
        var command = Command(Setting(
            "libx264", (VideoSettingLatency.OptionKey, "1"), (VideoSettingQuality.OptionKey, "High")));

        // ffmpeg stops on an option it does not know: "-lowlatency 1" was a live that never started.
        Assert.DoesNotContain("-lowlatency", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-encoderquality", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null, null, true)]
    [InlineData(false, null, null, false)]
    [InlineData(false, "veryfast", null, false)]
    [InlineData(false, null, "film", false)]
    [InlineData(false, "veryfast", " ", false)]
    [InlineData(false, "veryfast", "film", true)]
    public void WithoutLowLatencyThePresetAndTheTuneAreRequired(bool lowLatency, string? preset, string? tune, bool complete)
    {
        var form = new VideoSettingForm
        {
            Title = "mine",
            VideoCodec = 27,
            VideoCodecName = "libx264",
            PixelFormat = 0,
            VideoBitrate = 6_000_000,
            GopSize = 2,
            VideoFormat = "flv",
            AudioCodec = 86018,
            AudioBitrate = 128_000,
            LowLatency = lowLatency,
            Preset = preset,
            Tune = tune
        };

        Assert.Equal(complete, form.IsComplete);
    }

    [Fact]
    public void TheFormKeepsTheLevel()
    {
        var form = new VideoSettingForm { Title = "mine", Quality = EncoderQuality.High };
        var request = form.ToRequest();

        Assert.Contains(request.VideoOptions!, option => option!.Key == VideoSettingQuality.OptionKey && option.Value == "High");
        Assert.Equal(EncoderQuality.High, VideoSettingForm.From(request).Quality);

        // A setting from before the field is automatic.
        Assert.Equal(EncoderQuality.Auto, new VideoSettingForm().Quality);
        Assert.Equal(EncoderQuality.Auto, VideoSettingQuality.Parse(null));
        Assert.Equal(EncoderQuality.Auto, VideoSettingQuality.Parse("nonsense"));
    }

    [Fact]
    public async Task Auto_GetsTheBalancedLevelOnAMachineThatKeepsUp()
    {
        using var database = new TemporaryDatabase();
        var runs = new List<string>();
        var tuning = Tuning(database, arguments =>
        {
            runs.Add(string.Join(' ', arguments));
            return Task.FromResult(true);
        });

        var setting = Setting("h264_nvenc");
        var quality = await tuning.ResolveAsync(setting, 1920, 1080, 30, CancellationToken.None);
        var again = await tuning.ResolveAsync(setting, 1920, 1080, 30, CancellationToken.None);

        Assert.Equal(EncoderQuality.Balanced, quality);
        Assert.Equal(EncoderQuality.Balanced, again);

        // Measured once, at the size of the live, on the balanced preset.
        var trial = Assert.Single(runs);
        Assert.Contains("testsrc2=size=1920x1080:rate=30", trial, StringComparison.Ordinal);
        Assert.Contains("-preset p4", trial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_FallsBackToTheLightLevelOnAGpuThatDoesNotKeepUp()
    {
        using var database = new TemporaryDatabase();
        var tuning = Tuning(database, async arguments =>
        {
            // Three seconds of picture in two seconds at p4 is 1.5x: not the headroom a live needs.
            if (arguments.Contains("p4"))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return true;
        });

        var quality = await tuning.ResolveAsync(Setting("h264_nvenc"), 1280, 720, 30, CancellationToken.None);

        Assert.Equal(EncoderQuality.Light, quality);
    }

    [Fact]
    public async Task ALevelChosenByTheUserIsNeverMeasured()
    {
        using var database = new TemporaryDatabase();
        var tuning = Tuning(database, _ => throw new InvalidOperationException("nothing should run"));

        var quality = await tuning.ResolveAsync(
            Setting("h264_nvenc", (VideoSettingQuality.OptionKey, "High")), 1920, 1080, 30, CancellationToken.None);

        Assert.Equal(EncoderQuality.High, quality);
    }

    [Fact]
    public async Task TheDefaultsGoToTheGpuAndLeaveTheSettingsOfTheUserAlone()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();
        var mine = Setting("libx264", ("preset", "slow"));
        mine.DefaultPlatformConfiguration = VideoSettingForm.CustomPlatform;
        mine.IsVideoAndAudioSettingActive = true;
        mine.IsDefaultConfiguration = false;
        var mineId = settings.Insert(mine);

        var tuning = Tuning(database, arguments => Task.FromResult(arguments.Contains("h264_nvenc")));
        await tuning.TuneDefaultsAsync(CancellationToken.None);

        var defaults = settings.FindAll(new Dictionary<string, string>())
            .Where(setting => setting.DefaultPlatformConfiguration is "Twitch" or "Youtube" or "Kick" or "Facebook Gaming")
            .ToList();
        // A default and a low CPU one for each of the four platforms.
        Assert.Equal(8, defaults.Count);
        Assert.All(defaults, setting =>
        {
            Assert.Equal("h264_nvenc", setting.VideoCodecName);
            Assert.Equal(EncoderQuality.Auto, VideoSettingQuality.Of(setting));

            // tune zerolatency is a value NVENC refuses, and the level replaces the preset.
            Assert.DoesNotContain(setting.VideoSettingsOptions, option => option.Key is "preset" or "tune" or "x264-params");
        });

        var untouched = settings.FindById(mineId)!;
        Assert.Equal("libx264", untouched.VideoCodecName);
        Assert.Contains(untouched.VideoSettingsOptions, option => option.Key == "preset" && option.Value == "slow");
    }

    [Fact]
    public async Task TheDefaultsAreLeftAloneWhenFfmpegDoesNotRun()
    {
        using var database = new TemporaryDatabase();
        var settings = database.Repository<VideoSettingRepository>();
        var before = settings.FindAll(new Dictionary<string, string>())
            .Select(setting => (setting.VideoCodecName, setting.VideoSettingsOptions.Count))
            .ToList();

        await Tuning(database, _ => Task.FromResult(false)).TuneDefaultsAsync(CancellationToken.None);

        var after = settings.FindAll(new Dictionary<string, string>())
            .Select(setting => (setting.VideoCodecName, setting.VideoSettingsOptions.Count))
            .ToList();
        Assert.Equal(before, after);
    }

    private static EncoderTuningService Tuning(TemporaryDatabase database, Func<IReadOnlyList<string>, Task<bool>> run) =>
        new(
            new FfmpegToolLocator("ffmpeg", "ffprobe"),
            database.Repository<VideoSettingRepository>(),
            NullLogger<EncoderTuningService>.Instance,
            run);
}
