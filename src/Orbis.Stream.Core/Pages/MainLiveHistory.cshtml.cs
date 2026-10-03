using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLiveHistoryModel(VideoService videos, SettingService settings) : OrbisPageModel
{
    public const int PageSize = 20;

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageIndex { get; set; }

    public SpringPage<VideoRequest> Videos { get; private set; } = null!;

    public bool AutoCleanupEnabled { get; private set; }
    public int AutoCleanupIntervalMonths { get; private set; }
    public int AutoCleanupOlderThanMonths { get; private set; }

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

        Videos = videos.GetAllVideoList(
            new Dictionary<string, string>(),
            new PageRequest(Math.Max(PageIndex, 0), PageSize, [new SortOrder("startDateLive", true)]));
    }

    /// <summary>
    /// Saves the cleanup settings, and runs the cleanup straight away when the button that asked for
    /// it is the one that runs: the picks on screen are the ones it works on, so they are stored
    /// first. Both outcomes land on the same InfoBar, the last one overwriting the first.
    /// </summary>
    public IActionResult OnPostSaveAutoCleanup(bool enabled, int intervalMonths, int olderThanMonths, bool runNow = false)
    {
        if (!Run(() => settings.SaveAutoCleanup(enabled, intervalMonths, olderThanMonths)))
        {
            return RedirectToPage(new { p = PageIndex });
        }

        if (runNow)
        {
            Run(() => videos.DeleteOldLiveHistory(olderThanMonths));
        }

        return RedirectToPage(new { p = PageIndex });
    }

    public IActionResult OnPostDelete(long id)
    {
        Run(() => videos.DeleteLiveHistory(id));
        return RedirectToPage(new { p = PageIndex });
    }
}
