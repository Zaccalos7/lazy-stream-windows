using Microsoft.AspNetCore.Mvc;

namespace Orbis.Stream.Core.Pages;

public sealed class MainLayoutModel : OrbisPageModel
{
    [BindProperty(SupportsGet = true)]
    public long? Edit { get; set; }

    public void OnGet()
    {
    }
}
