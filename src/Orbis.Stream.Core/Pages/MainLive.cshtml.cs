using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Hosting;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLiveModel(
    VideoService videos,
    SettingService settings,
    VideoSettingService videoSettings,
    StreamingService streaming,
    LiveChangeNotifier notifier,
    RequestValidator validator,
    Localizer localizer) : OrbisPageModel
{
    public const int PageSize = 10;

    [BindProperty(SupportsGet = true)]
    public string? LiveStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ChannelName { get; set; }

    /// <summary>Zero-based page (<c>page</c> is a reserved route value in Razor Pages).</summary>
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageIndex { get; set; }

    public SpringPage<VideoRequest> Videos { get; private set; } = null!;

    public IReadOnlyCollection<string> Channels { get; private set; } = [];

    public IReadOnlyList<ChannelResponse> Locked { get; private set; } = [];

    public IReadOnlyList<VideoSettingsRequest> ActiveSettings { get; private set; } = [];

    public IReadOnlyList<SettingResponse> ActiveConfigurations { get; private set; } = [];

    /// <summary>Video whose setting is being linked (<c>?link={pkid}</c>).</summary>
    public int? LinkPkid { get; private set; }

    [BindProperty]
    public VideoSettingForm? Form { get; set; }

    /// <summary>
    /// The change the rows on the page were drawn from, so that the push channel sends this page
    /// only the changes that happened after it, and not the one that is already on the screen.
    /// </summary>
    public long ChangeVersion => notifier.Version;

    /// <summary>The side bar shortcut opens the wizard on arrival (<c>?start=1</c>).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Start { get; set; }

    public bool OpenStart => !string.IsNullOrEmpty(Start);

    public object Filters => new { LiveStatus, ChannelName, p = PageIndex };

    /// <summary>
    /// The filters (and, when the form acts on a row, its key) as route data, so every action
    /// comes back to the same view. Both must travel in <c>asp-all-route-data</c>: that attribute
    /// replaces the whole route value dictionary, so a sibling <c>asp-route-pkid</c> would be
    /// dropped and the handler would run with <c>pkid = 0</c>.
    /// </summary>
    public Dictionary<string, string> FilterRoute => RouteOf();

    /// <summary>Filters plus the <c>pkid</c> of the row the form acts on.</summary>
    public Dictionary<string, string> RowRoute(int? pkid) => RouteOf("pkid", pkid);

    /// <summary>Filters plus the <c>videoKey</c> of the locked video the unlock form acts on.</summary>
    public Dictionary<string, string> UnlockRoute(int? videoKey) => RouteOf("videoKey", videoKey);

    private Dictionary<string, string> RouteOf(string? key = null, int? value = null)
    {
        var route = new Dictionary<string, string>
        {
            ["liveStatus"] = LiveStatus ?? string.Empty,
            ["channelName"] = ChannelName ?? string.Empty,
            ["p"] = PageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (key is not null)
        {
            route[key] = (value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return route;
    }

    public void OnGet(int? link)
    {
        LoadVideos();
        Channels = settings.RetrieveChannel(new Dictionary<string, string>());
        Locked = videos.GetLockedVideos();
        ActiveSettings = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["isVideoAndAudioSettingActive"] = "true" });
        ActiveConfigurations = settings.RetrieveSettings(new Dictionary<string, string> { ["isActive"] = "true" });

        if (link is not null)
        {
            LinkPkid = link;
            Form = new VideoSettingForm();
        }
    }

    public PartialViewResult OnGetRows()
    {
        LoadVideos();
        return Partial("_LiveRows", this);
    }

    public IActionResult OnPostStop(int pkid)
    {
        Try(() =>
        {
            streaming.StopVideoStreamingByPkid(pkid);
            SetNotice(NoticeKind.Success, localizer.PrintMessage("live.stopped"));
            return true;
        });
        return RedirectToPage(Filters);
    }

    /// <summary>Play of a card: streams again the videos of the same live history, from where the
    /// interrupted one was stopped.</summary>
    public IActionResult OnPostReplay(int pkid)
    {
        Try(() =>
        {
            var video = videos.FindVideo(pkid);
            validator.RequireVideo(video);
            return Run(() => streaming.StartVideo(video));
        });
        return RedirectToPage(Filters);
    }

    /// <summary>Restart of a card: the same playlist, but from the first video and not from where
    /// the last interruption left it.</summary>
    public IActionResult OnPostRestart(int pkid)
    {
        Try(() =>
        {
            var video = videos.FindVideo(pkid);
            validator.RequireVideo(video);

            if (video.VideoLiveHistory is { } history && history.Pkid is { } historyPkid)
            {
                streaming.RestartFromBeginning(historyPkid);
            }

            return Run(() => streaming.StartVideo(video));
        });
        return RedirectToPage(Filters);
    }

    public IActionResult OnPostDelete(int pkid)
    {
        Run(() => videos.DeleteVideo(pkid));

        // Deleting the only row of the last page would leave the view on a page that no longer exists.
        LoadVideos();
        if (Videos.Content.Count == 0 && PageIndex > 0)
        {
            PageIndex = Math.Max(Videos.Page.TotalPages - 1, 0);
        }

        return RedirectToPage(Filters);
    }

    public IActionResult OnPostUnlock(int videoKey)
    {
        Run(() => videos.UnlockVideo(videoKey));
        return RedirectToPage(Filters);
    }

    public IActionResult OnPostLink(int pkid)
    {
        if (Form is null || !Form.IsComplete)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
        }
        else
        {
            Run(() => videoSettings.LinkAndSaveSettingsVideo(Form.ToRequest(), pkid));
        }

        return RedirectToPage(Filters);
    }

    /// <summary>The wizard: one active video setting plus one active streaming configuration.</summary>
    public IActionResult OnPostStart(int settingId, int configurationId)
    {
        var setting = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["id"] = settingId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();
        var configuration = settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();
        if (setting is null || configuration is null)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
            return RedirectToPage(Filters);
        }

        var request = new StartLiveRequest(
            configuration.StreamUrl,
            configuration.StreamKey,
            configuration.VideoFolder,
            configuration.PlatformStreamName,
            configuration.ChannelName,
            setting);

        var started = Try(() =>
        {
            validator.RequireStartLive(request);
            return Run(() => streaming.StartLive(request));
        });

        return started ? RedirectToPage("/Countdown") : RedirectToPage(Filters);
    }

    private void LoadVideos()
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(LiveStatus))
        {
            filters["liveStatus"] = LiveStatus;
        }

        if (!string.IsNullOrEmpty(ChannelName))
        {
            filters["channelName"] = ChannelName;
        }

        Videos = videos.GetAllVideoList(filters, new PageRequest(Math.Max(PageIndex, 0), PageSize, [new SortOrder("startDateLive", true)]));
    }
}
