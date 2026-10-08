using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLiveHistoryModel(VideoService videos, SettingService settings) : OrbisPageModel
{
    public const int PageSize = 20;

    /// <summary>
    /// The statuses the pick of the page offers, in the order the live grid lists them. A live that
    /// has ended is in here as much as one that failed: the history is mostly of lives that are
    /// over, and a pick that left them out would empty the table it is meant to narrow.
    /// </summary>
    // Spelled out with the namespace: inside the class the name LiveStatus is the pick of the page,
    // which hides the enum of the same name from every expression here.
    public static readonly Domain.LiveStatus[] Statuses =
    [
        Domain.LiveStatus.Live,
        Domain.LiveStatus.Offline,
        Domain.LiveStatus.Ended,
        Domain.LiveStatus.Error,
        Domain.LiveStatus.Stopped
    ];

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageIndex { get; set; }

    /// <summary>The status of the rows to show (<c>LIVE</c>, <c>ENDED</c>, …); null is all of them.</summary>
    [BindProperty(SupportsGet = true)]
    public string? LiveStatus { get; set; }

    /// <summary>The channel the rows to show were streamed to; null is all of them.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ChannelName { get; set; }

    /// <summary>
    /// A word of the title to look for. Null or empty is every title: an empty search box is not a
    /// search for the empty string, and a LIKE for <c>%%</c> would cost a scan of the table to
    /// match all of it.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? TitleSearch { get; set; }

    public SpringPage<VideoRequest> Videos { get; private set; } = null!;

    /// <summary>The channels the pick offers, as the videos of the past have them.</summary>
    public IReadOnlyList<string> Channels { get; private set; } = [];

    public bool AutoCleanupEnabled { get; private set; }
    public int AutoCleanupIntervalMonths { get; private set; }
    public int AutoCleanupOlderThanMonths { get; private set; }

    /// <summary>True while one of the three picks is narrowing the table, so it can say so.</summary>
    public bool Filtered =>
        !string.IsNullOrWhiteSpace(LiveStatus)
        || !string.IsNullOrWhiteSpace(ChannelName)
        || !string.IsNullOrWhiteSpace(TitleSearch);

    /// <summary>
    /// The picks, and optionally a page, as the route of a link of the page. A pick that says
    /// nothing is left out rather than sent empty: an empty value in the address is a filter the
    /// next page cannot tell from a channel that is really called nothing.
    /// </summary>
    public Dictionary<string, string> Link(int? page = null)
    {
        var route = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(LiveStatus))
        {
            route["liveStatus"] = LiveStatus;
        }

        if (!string.IsNullOrWhiteSpace(ChannelName))
        {
            route["channelName"] = ChannelName;
        }

        var search = TitleSearch?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            route["titleSearch"] = search;
        }

        if (page is { } index)
        {
            route["p"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return route;
    }

    /// <summary>
    /// The picks, the page and the row the form acts on, as route data. It travels whole rather than
    /// beside <c>asp-route-id</c>: that attribute replaces the whole route value dictionary, so a
    /// sibling of it would be dropped and the handler would run with <c>id = 0</c>.
    /// </summary>
    public Dictionary<string, string> DeleteRoute(long id)
    {
        var route = Link(PageIndex);
        route["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return route;
    }

    public void OnGet()
    {
        var configs = settings.RetrieveSettings(new Dictionary<string, string>());
        var activeConfig = configs.FirstOrDefault(c => c.IsActive == true);
        if (activeConfig != null)
        {
            AutoCleanupEnabled = activeConfig.AutoCleanupEnabled;
            AutoCleanupIntervalMonths = activeConfig.AutoCleanupIntervalMonths;
            AutoCleanupOlderThanMonths = activeConfig.AutoCleanupOlderThanMonths;
        }

        Channels = videos.GetVideoChannelNames();
        Videos = videos.GetAllVideoList(
            Filters(),
            new PageRequest(Math.Max(PageIndex, 0), PageSize, [new SortOrder("startDateLive", true)]));
    }

    /// <summary>
    /// The picks as the filters of the query. Each one is left out when it says nothing, for the
    /// reason <see cref="Link"/> gives: the endpoint answers an empty value with the rows that have
    /// an empty channel or an empty title, which is a different question from "no pick".
    /// </summary>
    private Dictionary<string, string> Filters()
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(LiveStatus))
        {
            filters["liveStatus"] = LiveStatus;
        }

        if (!string.IsNullOrWhiteSpace(ChannelName))
        {
            filters["channelName"] = ChannelName;
        }

        var search = TitleSearch?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            filters[VideoRepository.TitleSearchFilter] = search;
        }

        return filters;
    }

    /// <summary>
    /// Saves the cleanup settings of the active configuration. Running the cleanup is not here: it
    /// walks over the videos one at a time and the page follows that walk, so it is queued on its
    /// own and watched from the popover (<see cref="LiveHistoryCleanupService"/>).
    /// </summary>
    public IActionResult OnPostSaveAutoCleanup(bool enabled, int intervalMonths, int olderThanMonths)
    {
        Run(() => settings.SaveAutoCleanup(enabled, intervalMonths, olderThanMonths));
        return RedirectToPage(Link(PageIndex));
    }

    public IActionResult OnPostDelete(long id)
    {
        Run(() => videos.DeleteLiveHistory(id));
        return RedirectToPage(Link(PageIndex));
    }
}