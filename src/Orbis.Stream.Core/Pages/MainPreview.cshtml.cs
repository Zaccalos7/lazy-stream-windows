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
/// second. The canvas a live is composed on is a step of the live wizard, not of this page.
/// </summary>
public sealed class MainPreviewModel(
    LivePreviewService preview,
    StreamingService streaming,
    Localizer localizer) : OrbisPageModel
{
    private LiveSnapshot? _snapshot;

    /// <summary>The live to watch (<c>?live=</c>). Left out, it is the one that started last.</summary>
    [BindProperty(SupportsGet = true, Name = "live")]
    public int? Live { get; set; }

    /// <summary>The old address of the canvas (<c>?compose=1</c>): it lives in the live wizard now.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Compose { get; set; }

    public LiveSnapshot Snapshot => _snapshot ??= preview.Snapshot(Live);

    /// <summary>
    /// Where the platform icon of the live leads, when the platform is one we know: the channel on
    /// Twitch, the live control room of YouTube Studio on YouTube.
    /// </summary>
    public string? LiveUrl =>
        LiveLinkView.UrlOf(Snapshot.StreamUrl, Snapshot.ChannelName, Snapshot.PlatformStreamName, Snapshot.PlatformVideoId);

    public string LivePlatform => LiveLinkView.LabelOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl)) ?? string.Empty;

    public string LiveGlyph => LiveLinkView.MarkupOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl));

    /// <summary>The class that colours the mark of the platform.</summary>
    public string LiveGlyphClass => LiveLinkView.ClassOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl));

    /// <summary>Where the light picture of a live is fetched from, one frame at a time.</summary>
    public static string FramesUrlOf(int pkid) => $"/preview/live/{pkid}/frame";

    public IActionResult OnGet() =>
        string.IsNullOrEmpty(Compose) ? Page() : Redirect("/orbis/mainLive?start=1");

    public IActionResult OnPostStop(int pkid)
    {
        Try(() =>
        {
            streaming.StopVideoStreamingByPkid(pkid);
            SetNotice(NoticeKind.Success, localizer.PrintMessage("live.stopped"));
            return true;
        });
        return RedirectToPage();
    }

    public IActionResult OnPostSpot(int videoKey, string spotPath)
    {
        Try(() =>
        {
            streaming.EnqueueSpot(spotPath, videoKey);
            SetNotice(NoticeKind.Success, localizer.PrintMessage("live.yielded") ?? "Spot iniziato");
            return true;
        });
        return RedirectToPage(new { live = videoKey > 0 ? (int?)videoKey : Live });
    }
}
