using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.SystemInfo;

namespace Orbis.Stream.Core.Pages;

public sealed class MainTaskManagerModel(SystemInfoService systemInfo) : OrbisPageModel
{
    /// <summary>What the machine is: read once, the meters above are the ones that keep moving.</summary>
    public IReadOnlyList<SystemFact> Facts { get; private set; } = [];

    /// <summary>
    /// Nothing is measured here: a CPU sample takes half a second and the temperature goes through
    /// WMI, and that wait would hold the whole navigation with no sign of it. The meters are drawn
    /// empty, in their loading, and the push channel fills them with its first sample.
    /// </summary>
    public void OnGet()
    {
    }

    public PartialViewResult OnGetFacts()
    {
        Facts = systemInfo.GetSystemFacts();
        return Partial("_SystemInfo", this);
    }
}
