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

    public IActionResult OnPostDelete(long id)
    {
        var success = Run(() => videos.DeleteLiveHistory(id));
        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest" || Request.Headers["Accept"].ToString().Contains("application/json") )
        {
            return success ? new OkResult() : new BadRequestResult();
        }
        return RedirectToPage(new { p = PageIndex });
    }
}
