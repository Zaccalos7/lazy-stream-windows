using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

/// <summary>
/// The preview of a live. The page only reads the state when it is asked for: from there on the
/// push channel repaints it, because a live that moves every second must not cost a request every
/// second. With no live running, or when asked (<c>?compose=1</c>), it is the canvas where the
/// next live is put together, source by source, before anything goes on air.
/// </summary>
public sealed class MainPreviewModel(
    LivePreviewService preview,
    SettingService settings,
    VideoSettingService videoSettings,
    StreamingService streaming,
    RequestValidator validator,
    Localizer localizer) : OrbisPageModel
{
    private LiveSnapshot? _snapshot;

    /// <summary>The live to watch (<c>?live=</c>). Left out, it is the one that started last.</summary>
    [BindProperty(SupportsGet = true, Name = "live")]
    public int? Live { get; set; }

    /// <summary>Opens the canvas even while a live is running (<c>?compose=1</c>).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Compose { get; set; }

    /// <summary>The scene the canvas opens on (<c>?scene=</c>), after a save or a failed start.</summary>
    [BindProperty(SupportsGet = true, Name = "scene")]
    public long? Scene { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? SettingId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? ConfigurationId { get; set; }

    public LiveSnapshot Snapshot => _snapshot ??= preview.Snapshot(Live);

    public bool IsComposing => !Snapshot.IsLive || !string.IsNullOrEmpty(Compose);

    public IReadOnlyList<VideoSettingsRequest> ActiveSettings { get; private set; } = [];

    public IReadOnlyList<SettingResponse> ActiveConfigurations { get; private set; } = [];

    /// <summary>The page of the live on its platform, when the platform is one we know.</summary>
    public string? LiveUrl =>
        LiveLinkView.UrlOf(Snapshot.StreamUrl, Snapshot.ChannelName, Snapshot.PlatformStreamName);

    public string LivePlatform => LiveLinkView.LabelOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl)) ?? string.Empty;

    public string LiveGlyph => LiveLinkView.GlyphOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl));

    /// <summary>Where the file of a live is served from, for the player.</summary>
    public static string VideoUrlOf(int pkid) => $"/preview/live/{pkid}/video";

    public void OnGet()
    {
        if (!IsComposing)
        {
            return;
        }

        // The same choice the live wizard offers: an encoder setting and a destination. The scene
        // takes the place of the folder, so it is the only step the canvas adds.
        ActiveSettings = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["isVideoAndAudioSettingActive"] = "true" });
        ActiveConfigurations = settings.RetrieveSettings(new Dictionary<string, string> { ["isActive"] = "true" });
    }

    public IActionResult OnPostStartScene(long scenePkid, int settingId, int configurationId)
    {
        var setting = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["id"] = settingId.ToString(CultureInfo.InvariantCulture) }).FirstOrDefault();
        var configuration = settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = configurationId.ToString(CultureInfo.InvariantCulture) }).FirstOrDefault();
        if (setting is null || configuration is null)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
            return RedirectToPage(new { compose = "1", scene = scenePkid });
        }

        var request = new StartSceneLiveRequest(
            scenePkid,
            configuration.StreamUrl,
            configuration.StreamKey,
            configuration.PlatformStreamName,
            configuration.ChannelName,
            setting);

        var started = Try(() =>
        {
            validator.RequireStartSceneLive(request);
            return Run(() => streaming.StartSceneLive(request));
        });

        return started ? RedirectToPage("/Countdown") : RedirectToPage(new { compose = "1", scene = scenePkid });
    }
}
