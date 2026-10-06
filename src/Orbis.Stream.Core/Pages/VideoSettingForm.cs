using Microsoft.AspNetCore.Mvc.Rendering;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Pages;

/// <summary>
/// The video + audio settings form (VideoAndAudioSettingComponent of the React build), bound by
/// the video settings page and by the "link a setting" dialog of the live page.
/// </summary>
public sealed class VideoSettingForm
{
    public const string CustomPlatform = "custom";

    public int? Id { get; set; }

    public string? Title { get; set; }

    public int? VideoCodec { get; set; }

    public string? VideoCodecName { get; set; }

    public int? PixelFormat { get; set; }

    public int? VideoBitrate { get; set; }

    public string? VideoFormat { get; set; }

    public int? GopSize { get; set; }

    /// <summary>Resolution asked to the encoder; the pair is empty when the source keeps its own.</summary>
    public int? VideoWidth { get; set; }

    public int? VideoHeight { get; set; }

    public double? FrameRate { get; set; }

    public int? AudioCodec { get; set; }

    public int? AudioBitrate { get; set; }

    public string? Preset { get; set; }

    public string? Tune { get; set; }

    /// <summary>
    /// Whether the encoder is asked for the smallest delay it can manage: no lookahead, no
    /// scenecut, no encoder delay. On by default, and worth turning off for a re-stream, because
    /// without a lookahead the encoder cannot see the forced keyframe coming and empties its rate
    /// control buffer on it, which starves the frames after every keyframe. The relay paces what
    /// comes out anyway, so the jitter it costs buys nothing.
    /// </summary>
    public bool LowLatency { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Everything but the extra options is required (the React form let the GOP size empty, but the column is NOT NULL).</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Title)
        && VideoCodec is not null
        && !string.IsNullOrWhiteSpace(VideoCodecName)
        && PixelFormat is not null
        && VideoBitrate is not null
        && GopSize is not null
        && !string.IsNullOrWhiteSpace(VideoFormat)
        && AudioCodec is not null
        && AudioBitrate is not null
        && IsResolutionComplete
        && FrameRate is not > 480;

    /// <summary>The two halves of a resolution travel together: one alone is a half typed form.</summary>
    public bool IsResolutionComplete => VideoWidth is null == (VideoHeight is null);

    public static VideoSettingForm From(VideoSettingsRequest setting) => new()
    {
        Id = setting.Id,
        Title = setting.Title,
        VideoCodec = setting.VideoCodec,
        VideoCodecName = setting.VideoCodecName,
        PixelFormat = setting.PixelFormat,
        VideoBitrate = setting.VideoBitrate,
        VideoFormat = setting.VideoFormat,
        GopSize = setting.GopSize,
        VideoWidth = setting.VideoWidth,
        VideoHeight = setting.VideoHeight,
        FrameRate = setting.FrameRate,
        AudioCodec = setting.AudioSettingRecord?.AudioCodec,
        AudioBitrate = setting.AudioSettingRecord?.AudioBitrate,
        Preset = Option(setting, "preset"),
        Tune = Option(setting, "tune"),
        LowLatency = VideoSettingLatency.IsOn(Option(setting, VideoSettingLatency.OptionKey)),
        IsActive = setting.IsVideoAndAudioSettingActive ?? false
    };

    /// <summary>User settings are always "custom": only the seeded Twitch/Youtube rows are defaults.</summary>
    public VideoSettingsRequest ToRequest() => new(
        Id,
        Title?.Trim(),
        VideoCodec,
        VideoCodecName,
        PixelFormat,
        VideoBitrate,
        null,
        IsActive,
        GopSize,
        [.. new[] { ("preset", Preset), ("tune", Tune), (VideoSettingLatency.OptionKey, LowLatency ? "1" : "0") }
            .Where(option => !string.IsNullOrEmpty(option.Item2))
            .Select(option => new VideoOptionRequest(option.Item1, option.Item2))],
        VideoFormat,
        new AudioSettingsRequest(AudioCodec, AudioBitrate),
        false,
        CustomPlatform,
        VideoWidth,
        VideoHeight,
        FrameRate);

    private static string? Option(VideoSettingsRequest setting, string key) =>
        setting.VideoOptions?.FirstOrDefault(option => option?.Key == key)?.Value;

    public static IEnumerable<SelectListItem> Items(IEnumerable<MediaOption> options) =>
        options.Select(option => new SelectListItem(option.Label, option.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static IEnumerable<SelectListItem> CodecNameItems() =>
        FfmpegCodecCatalog.VideoCodecNames.SelectMany(vendor =>
        {
            var group = new SelectListGroup { Name = vendor.Vendor };
            return vendor.Names.Select(name => new SelectListItem($"Codec {vendor.Vendor} ({name})", name) { Group = group });
        });

    public static IEnumerable<SelectListItem> FormatItems() =>
        FfmpegCodecCatalog.VideoFormats.Select(format => new SelectListItem(format.ToUpperInvariant(), format));

    public static IEnumerable<SelectListItem> PresetItems() =>
        FfmpegCodecCatalog.Presets.Select(preset => new SelectListItem($"{char.ToUpperInvariant(preset[0])}{preset[1..]} ({preset})", preset));

    public static IEnumerable<SelectListItem> TuneItems() =>
        FfmpegCodecCatalog.Tunes.Select(tune => new SelectListItem($"{tune.Label} ({tune.Value})", tune.Value));

    /// <summary>Bitrate as the cards show it: millions above 999 999 bps.</summary>
    public static string FormatBitrate(int? bitrate, string millionLabel) => bitrate switch
    {
        null => "0",
        > 999_999 => $"{bitrate.Value / 1_000_000d:0.##} {millionLabel}",
        _ => bitrate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
