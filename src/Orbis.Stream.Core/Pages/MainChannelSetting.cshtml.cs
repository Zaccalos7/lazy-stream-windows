using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

/// <summary>Streaming configurations (backend "settings"): one per platform, channel and key.</summary>
public sealed class MainChannelSettingModel(SettingService settings, RequestValidator validator) : OrbisPageModel
{
    /// <summary>RTMP ingest of each platform, applied when the URL is left empty.</summary>
    public static readonly IReadOnlyList<(string Value, string Label, string StreamUrl)> Platforms =
    [
        ("twitch", "Twitch", "rtmp://live.twitch.tv/app"),
        ("youtube", "YouTube", "rtmps://a.rtmp.youtube.com/live2")
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
            ? new SettingResponse(null, null, null, null, null, null, null, null, true, null)
            : int.TryParse(edit, out var id) ? settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = edit! }).FirstOrDefault(setting => setting.Id == id) : null;
    }

    public IActionResult OnPostSave(
        int? id,
        string? platformStreamName,
        string? channelName,
        string? streamUrl,
        string? streamKey,
        string? description,
        string? videoFolder)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            streamUrl = Platforms.FirstOrDefault(platform => platform.Value == platformStreamName).StreamUrl;
        }

        var request = new SettingRequest(
            streamUrl?.Trim(),
            streamKey?.Trim(),
            platformStreamName,
            description?.Trim(),
            videoFolder?.Trim(),
            id is null ? true : null,
            channelName?.Trim());

        Try(() =>
        {
            validator.RequireSetting(request);
            return Run(() => id is { } existing ? settings.ModifySetting(existing, request) : settings.AddNewConfiguration(request));
        });

        return BackToList();
    }

    public IActionResult OnPostToggle(int id, bool current)
    {
        Run(() => settings.ModifySetting(id, new SettingRequest(null, null, null, null, null, !current, null)));
        return BackToList();
    }

    public IActionResult OnPostDelete(int id)
    {
        Run(() => settings.DeleteAStreamingSetting(id));
        return BackToList();
    }

    private RedirectToPageResult BackToList() => RedirectToPage(new { Platform, Active });
}
