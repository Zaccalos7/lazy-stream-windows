using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
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

    public SpringPage<LiveRow> Videos { get; private set; } = null!;

    public IReadOnlyCollection<string> Channels { get; private set; } = [];

    public IReadOnlyList<ChannelResponse> Locked { get; private set; } = [];

    public IReadOnlyList<VideoSettingsRequest> ActiveSettings { get; private set; } = [];

    public IReadOnlyList<SettingResponse> ActiveConfigurations { get; private set; } = [];

    /// <summary>Video whose setting is being linked (<c>?link={pkid}</c>).</summary>
    public int? LinkPkid { get; private set; }

    /// <summary>Playlist whose setting is being linked (<c>?linkHistory={history}</c>).</summary>
    public long? LinkHistory { get; private set; }

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

    /// <summary>The composer comes back open on this scene, with the two picks of the first step,
    /// when a start was refused (<c>?compose={scenePkid}&amp;settingId=…&amp;configurationId=…</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "compose")]
    public long? ComposeScene { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? SettingId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? ConfigurationId { get; set; }

    /// <summary>The playlist wizard comes back open, with the folder it was given, when the folder
    /// was refused (<c>?playlist=1&amp;folder=…</c>): the user fixes the path instead of retyping it.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Playlist { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }

    public bool OpenPlaylist => !string.IsNullOrEmpty(Playlist);

    /// <summary>The playlist whose details dialog is open (<c>?details={historyPkid}</c>). It stays
    /// in the address after an action taken inside the dialog, so the dialog comes back with it.</summary>
    [BindProperty(SupportsGet = true)]
    public long? Details { get; set; }

    /// <summary>What the details dialog shows; null when no dialog is asked for, or the playlist is gone.</summary>
    public PlaylistDetails? OpenedPlaylist { get; private set; }

    public object Filters => new { LiveStatus, ChannelName, p = PageIndex, details = Details };

    /// <summary>
    /// The filters (and, when the form acts on a row, its key) as route data, so every action
    /// comes back to the same view. Both must travel in <c>asp-all-route-data</c>: that attribute
    /// replaces the whole route value dictionary, so a sibling <c>asp-route-pkid</c> would be
    /// dropped and the handler would run with <c>pkid = 0</c>.
    /// </summary>
    public Dictionary<string, string> FilterRoute => RouteOf();

    /// <summary>Filters plus the <c>pkid</c> of the row the form acts on.</summary>
    public Dictionary<string, string> RowRoute(int? pkid) => RouteOf("pkid", pkid);

    /// <summary>Filters plus the <c>pkid</c> of a video of the open details dialog, which comes back open.</summary>
    public Dictionary<string, string> DetailsRowRoute(int? pkid)
    {
        var route = RouteOf("pkid", pkid);
        route["details"] = (Details ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return route;
    }

    /// <summary>Filters plus the live history of the playlist the form acts on as a whole.</summary>
    public Dictionary<string, string> PlaylistRoute(long? history) => RouteOf("history", history);

    /// <summary>Filters plus the <c>videoKey</c> of the locked video the unlock form acts on.</summary>
    public Dictionary<string, string> UnlockRoute(int? videoKey) => RouteOf("videoKey", videoKey);

    private Dictionary<string, string> RouteOf(string? key = null, long? value = null)
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

    public void OnGet(int? link, long? linkHistory)
    {
        LoadVideos();
        LoadDetails();
        Channels = settings.RetrieveChannel(new Dictionary<string, string>());
        Locked = videos.GetLockedVideos();
        ActiveSettings = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["isVideoAndAudioSettingActive"] = "true" });
        ActiveConfigurations = settings.RetrieveSettings(new Dictionary<string, string> { ["isActive"] = "true" });

        if (link is not null || linkHistory is not null)
        {
            LinkPkid = link;
            LinkHistory = linkHistory;
            // The dialog opens on the setting the row already streams with: saving edits that one.
            VideoSettingsRequest? current = null;
            Try(() =>
            {
                current = videoSettings.FindSettingOf(link, linkHistory);
                return true;
            });
            Form = current is null ? new VideoSettingForm() : VideoSettingForm.From(current);
        }
    }

    public PartialViewResult OnGetRows()
    {
        LoadVideos();
        return Partial("_LiveRows", this);
    }

    /// <summary>The list of the details dialog, redrawn on every change like the rows are.</summary>
    public PartialViewResult OnGetDetailsRows()
    {
        LoadDetails();
        return Partial("_PlaylistVideos", this);
    }

    public IActionResult OnPostDeletePlaylist(long history)
    {
        Run(() => videos.DeletePlaylist(history));

        LoadVideos();
        if (Videos.Content.Count == 0 && PageIndex > 0)
        {
            PageIndex = Math.Max(Videos.Page.TotalPages - 1, 0);
        }

        return RedirectToPage(Filters);
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

    public IActionResult OnPostSpot(int videoKey, string spotPath)
    {
        return Try(() =>
        {
            streaming.EnqueueSpot(spotPath, videoKey);
            SetNotice(NoticeKind.Success, localizer.PrintMessage("live.yielded") ?? "Spot iniziato");
            return true;
        }) ? new OkResult() : RedirectToPage();
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

    /// <summary>"Start again from this video" of the playlist dialog: the ones before it count as
    /// streamed, and the playlist goes on air from it. The dialog stays open to watch it start.</summary>
    public IActionResult OnPostReplayFrom(int pkid)
    {
        Try(() =>
        {
            streaming.PrepareStartFrom(pkid);
            var video = videos.FindVideo(pkid);
            validator.RequireVideo(video);
            return Run(() => streaming.StartVideo(video));
        });
        return RedirectToPage(Filters);
    }

    public IActionResult OnPostUnlock(int videoKey)
    {
        Run(() => videos.UnlockVideo(videoKey));
        return RedirectToPage(Filters);
    }

    public IActionResult OnPostLink(int pkid, long? history)
    {
        if (Form is null || !Form.IsComplete)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
        }
        else if (history is not null)
        {
            Run(() => videoSettings.LinkAndSaveSettingsPlaylist(Form.ToRequest(), history.Value));
        }
        else
        {
            Run(() => videoSettings.LinkAndSaveSettingsVideo(Form.ToRequest(), pkid));
        }

        return RedirectToPage(Filters);
    }

    /// <summary>The wizard: one active video setting plus one active streaming configuration.</summary>
    /// <summary>
    /// The live wizard: the setting and the destination of the first step, and the scene the
    /// composer has just saved. The rows of the live keep every source of it (kind, target, place
    /// on the canvas), so a play or a restart later needs nothing else.
    /// </summary>
    public IActionResult OnPostStartScene(long scenePkid, int settingId, int configurationId)
    {
        var setting = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["id"] = settingId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();
        var configuration = settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();

        var started = false;
        if (setting is null || configuration is null)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
        }
        else
        {
            var request = new StartSceneLiveRequest(
                scenePkid,
                configuration.StreamUrl,
                configuration.StreamKey,
                configuration.PlatformStreamName,
                configuration.ChannelName,
                setting);

            started = Try(() =>
            {
                validator.RequireStartSceneLive(request);
                return Run(() => streaming.StartSceneLive(request));
            });
        }

        return started
            ? RedirectToPage(Filters)
            : RedirectToPage(new { LiveStatus, ChannelName, p = PageIndex, compose = scenePkid, settingId, configurationId });
    }

    /// <summary>The same wizard, with the folder picked here in place of the configuration's one.</summary>
    public IActionResult OnPostPlaylist(int settingId, int configurationId, string? videoFolder)
    {
        var request = StartRequestOf(settingId, configurationId, videoFolder ?? string.Empty);
        var started = request is not null && Try(() =>
        {
            validator.RequireStartLive(request);
            return Run(() => streaming.StartPlaylist(request));
        });

        return started
            ? RedirectToPage(Filters)
            : RedirectToPage(new { LiveStatus, ChannelName, p = PageIndex, playlist = 1, folder = videoFolder });
    }

    /// <summary>What the playlist wizard picked, as a start request; null (with the notice set) when
    /// either pick no longer exists.</summary>
    private StartLiveRequest? StartRequestOf(int settingId, int configurationId, string folder)
    {
        var setting = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["id"] = settingId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();
        var configuration = settings.RetrieveSettings(new Dictionary<string, string> { ["id"] = configurationId.ToString(System.Globalization.CultureInfo.InvariantCulture) }).FirstOrDefault();
        if (setting is null || configuration is null)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
            return null;
        }

        return new StartLiveRequest(
            configuration.StreamUrl,
            configuration.StreamKey,
            folder,
            configuration.PlatformStreamName,
            configuration.ChannelName,
            setting);
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

        Videos = videos.GetLivePage(
            LiveStatusExtensions.TryParseWireValue(LiveStatus, out var status) ? status : null,
            ChannelName,
            new PageRequest(Math.Max(PageIndex, 0), PageSize, [new SortOrder("startDateLive", true)]));
    }

    private void LoadDetails()
    {
        if (Details is not { } history)
        {
            return;
        }

        OpenedPlaylist = videos.GetPlaylist(history);
    }
}
