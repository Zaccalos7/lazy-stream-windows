using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

/// <summary>
/// The preview of a live. The page only reads the state when it is asked for: from there on the
/// push channel repaints it, because a live that moves every second must not cost a request every
/// second.
/// </summary>
public sealed class MainPreviewModel(LivePreviewService preview) : OrbisPageModel
{
    private LiveSnapshot? _snapshot;

    /// <summary>The live to watch (<c>?live=</c>). Left out, it is the one that started last.</summary>
    [BindProperty(SupportsGet = true, Name = "live")]
    public int? Live { get; set; }

    public LiveSnapshot Snapshot => _snapshot ??= preview.Snapshot(Live);

    /// <summary>The page of the live on its platform, when the platform is one we know.</summary>
    public string? LiveUrl =>
        LiveLinkView.UrlOf(Snapshot.StreamUrl, Snapshot.ChannelName, Snapshot.PlatformStreamName);

    public string LivePlatform => LiveLinkView.LabelOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl)) ?? string.Empty;

    public string LiveGlyph => LiveLinkView.GlyphOf(LiveLinkView.PlatformOf(Snapshot.StreamUrl));

    /// <summary>Where the file of a live is served from, for the player.</summary>
    public static string VideoUrlOf(int pkid) => $"/preview/live/{pkid}/video";
}
