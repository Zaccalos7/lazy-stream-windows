using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLayoutListModel(SceneService scenes) : OrbisPageModel
{
    public IReadOnlyList<SceneRequest> Layouts { get; private set; } = [];

    public void OnGet()
    {
        Layouts = scenes.GetLayouts();
    }

    public IActionResult OnPostDelete(long id)
    {
        Run(() => scenes.Delete(id));
        return RedirectToPage();
    }
}
