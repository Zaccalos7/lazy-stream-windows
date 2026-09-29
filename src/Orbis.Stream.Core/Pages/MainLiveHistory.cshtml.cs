using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLiveHistoryModel(VideoService videos) : OrbisPageModel
{
    public const int PageSize = 20;

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageIndex { get; set; }

    public SpringPage<VideoRequest> Videos { get; private set; } = null!;

    public void OnGet() => Videos = videos.GetAllVideoList(
        new Dictionary<string, string>(),
        new PageRequest(Math.Max(PageIndex, 0), PageSize, [new SortOrder("startDateLive", true)]));
}
