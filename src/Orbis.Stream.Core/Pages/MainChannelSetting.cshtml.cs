using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

/// <summary>Streaming configurations (backend "settings"): one per platform, channel and key.</summary>
public sealed class MainChannelSettingModel(SettingService settings, RequestValidator validator) : OrbisPageModel
{
    /// <summary>
    /// RTMP ingest of each platform, applied when the URL is left empty. YouTube is the RTMPS host
    /// (<c>a.rtmps</c>): the first versions paired the RTMPS scheme with the RTMP host, which only
    /// worked because ffmpeg does not check the certificate (it is issued for <c>*.rtmps</c>).
    /// </summary>
    public static readonly IReadOnlyList<(string Value, string Label, string StreamUrl)> Platforms =
    [
        ("twitch", "Twitch", "rtmp://live.twitch.tv/app"),
        // rtmps is served by a.rtmps.youtube.com: a.rtmp.youtube.com is the plain RTMP host, and its
        // certificate does not name it (see StreamPlatforms.NormalizeIngestUrl).
        ("youtube", "YouTube", "rtmps://a.rtmps.youtube.com/live2")
    ];

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
            ? new SettingResponse(null, null, null, null, null, null, null, null, true, null, false, 0, 0)
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
            streamUrl = Platforms.FirstOrDefault(platform => platform.Value == platformStreamName).StreamUrl;
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
