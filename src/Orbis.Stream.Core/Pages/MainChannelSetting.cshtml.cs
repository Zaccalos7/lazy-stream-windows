using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Pages;

/// <summary>
/// A platform the channel settings offer: the value stored on the configuration (and on the lives
/// started from it), the name the platform goes by, the ingest filled in when it is picked, and
/// the delivery a live gets there.
/// </summary>
/// <param name="PersonalIngest">
/// Whether the ingest can be the account's own (Kick, an IVS endpoint per channel) or the live's
/// own (Facebook, whose Live Producer and Graph API hand a broadcast an address of their own;
/// TikTok, whose LIVE Producer shows the server of the account's region) rather than one address
/// for everybody. Its preset is where the ingest usually is, the address to use is the one the
/// platform shows, and a live is recognised as one of this platform by the domain of its ingest
/// instead of by the preset.
/// </param>
public sealed record PlatformChoice(
    string Value, string Label, string StreamUrl, StreamPlatform Platform, bool PersonalIngest = false);

/// <summary>Streaming configurations (backend "settings"): one per platform, channel and key.</summary>
public sealed class MainChannelSettingModel(SettingService settings, RequestValidator validator) : OrbisPageModel
{
    /// <summary>
    /// RTMP ingest of each platform, applied when the URL is left empty. YouTube is the RTMPS host
    /// (<c>a.rtmps</c>): the first versions paired the RTMPS scheme with the RTMP host, which only
    /// worked because ffmpeg does not check the certificate (it is issued for <c>*.rtmps</c>).
    /// </summary>
    public static readonly IReadOnlyList<PlatformChoice> Platforms =
    [
        new("twitch", "Twitch", "rtmp://live.twitch.tv/app", StreamPlatform.Twitch),
        // rtmps is served by a.rtmps.youtube.com: a.rtmp.youtube.com is the plain RTMP host, and its
        // certificate does not name it (see StreamPlatforms.NormalizeIngestUrl).
        new("youtube", "YouTube", "rtmps://a.rtmps.youtube.com/live2", StreamPlatform.YouTube),
        // The IVS endpoint the Kick dashboard shows (RTMPS on 443, app "app"). An account can be
        // given one of its own, under the same network, which is why Kick is read off the domain.
        new("kick", "Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net/app", StreamPlatform.Kick, PersonalIngest: true),
        // The RTMPS ingest of Facebook Live, where Facebook Gaming lives go: RTMPS on 443 only, as
        // OBS lists it. Live Producer may show another host of Facebook's (live-api-s), and either
        // is recognised by its domain.
        new("facebook", "Facebook Gaming", "rtmps://rtmp-api.facebook.com:443/rtmp", StreamPlatform.Facebook, PersonalIngest: true),
        // A TikTok LIVE server as LIVE Producer shows it. The key that goes with it is good for one
        // live only, so it is not kept here: it is asked for whenever a live starts (LiveStreamKeys).
        new("tiktok", "TikTok", "rtmp://push-rtmp-l11-va01.tiktokcdn.com/stage", StreamPlatform.TikTok, PersonalIngest: true)
    ];

    /// <summary>
    /// The values of the platforms whose ingest is the account's or the live's own, as the page lists
    /// them to a script for the hint about the dashboard. TikTok has a hint of its own: its key.
    /// </summary>
    public static string PersonalIngests { get; } = string.Join(' ', Platforms
        .Where(platform => platform.PersonalIngest && !LiveStreamKeys.IsAskedFor(platform.Value, platform.StreamUrl))
        .Select(platform => platform.Value));

    /// <summary>The values of the platforms whose key is asked for at every live, as the page lists them to a script.</summary>
    public static string KeyEachLive { get; } = string.Join(' ', Platforms
        .Where(platform => LiveStreamKeys.IsAskedFor(platform.Value, platform.StreamUrl))
        .Select(platform => platform.Value));

    /// <summary>
    /// The platform of a configuration: the one its platform field names, otherwise the one its
    /// ingest belongs to; null for a destination this application does not know.
    /// </summary>
    public static PlatformChoice? ChoiceOf(string? platformStreamName, string? streamUrl = null)
    {
        var named = platformStreamName?.Trim();
        return Platforms.FirstOrDefault(platform => string.Equals(platform.Value, named, StringComparison.OrdinalIgnoreCase))
            ?? Platforms.FirstOrDefault(platform => platform.Value == LiveLinkView.PlatformOf(streamUrl));
    }

    public IReadOnlyList<SettingResponse> Configurations { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Platform { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Active { get; set; }

    /// <summary>The configuration in the dialog: <c>?edit=new</c> or <c>?edit={id}</c>.</summary>
    public SettingResponse? Editing { get; private set; }

    public void OnGet(string? edit)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Platform))
        {
            filters["platformStreamName"] = Platform;
        }

        if (Active is "true" or "false")
        {
            filters["isActive"] = Active;
        }

        Configurations = settings.RetrieveSettings(filters);

        Editing = edit == "new"
            // A new configuration publishes with ffmpeg: what the switch means on YouTube, and
            // nothing anywhere else (see SettingService).
            ? new SettingResponse(null, null, null, null, null, null, null, null, true, null, false, 0, 0, FfmpegSender: true)
            : int.TryParse(edit, out var id) ? settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = edit! }).FirstOrDefault(setting => setting.Id == id) : null;
    }

    public IActionResult OnPostSave(
        int? id,
        string? platformStreamName,
        string? channelName,
        string? streamUrl,
        string? streamKey,
        string? description,
        bool autoCleanupEnabled,
        int autoCleanupIntervalMonths,
        int autoCleanupOlderThanMonths,
        bool ffmpegSender)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            streamUrl = Platforms.FirstOrDefault(platform => platform.Value == platformStreamName)?.StreamUrl;
        }

        var request = new SettingRequest(
            streamUrl?.Trim(),
            streamKey?.Trim(),
            platformStreamName,
            // Optional: an empty box is stored as empty, so an edit can also clear it.
            description?.Trim() ?? string.Empty,
            // What a live streams is chosen on the canvas when it starts, not on the destination.
            null,
            id is null ? true : null,
            channelName?.Trim(),
            autoCleanupEnabled,
            autoCleanupIntervalMonths,
            autoCleanupOlderThanMonths,
            ffmpegSender);

        Try(() =>
        {
            validator.RequireSetting(request);
            return Run(() => id is { } existing ? settings.ModifySetting(existing, request) : settings.AddNewConfiguration(request));
        });

        return BackToList();
    }

public IActionResult OnPostToggle(int id, bool current)
    {
        // A toggle only flips IsActive. The auto cleanup fields are not nullable on the request,
        // so they cannot be told from "left alone" here; ModifySetting does not write them, which
        // is what keeps this from wiping the cleanup settings of the row being toggled.
        Run(() => settings.ModifySetting(id, new SettingRequest(null, null, null, null, null, !current, null, false, 0, 0)));
        return BackToList();
    }

    public IActionResult OnPostDelete(int id)
    {
        Run(() => settings.DeleteAStreamingSetting(id));
        return BackToList();
    }

    private RedirectToPageResult BackToList() => RedirectToPage(new { Platform, Active });
}
