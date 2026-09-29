using System.Collections;
using Microsoft.AspNetCore.Builder;
using Orbis.Stream.Core.Configuration;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Entry point helper shared by the WPF shell and the test-suite: resolves the runtime options
/// and builds the very same <see cref="WebApplication"/> the desktop application hosts.
/// </summary>
public static class OrbisHost
{
    public static OrbisRuntimeOptions ResolveOptions(string[]? args = null) =>
        OrbisRuntimeOptions.Resolve(args ?? [], ReadEnvironment());

    public static WebApplication Create(OrbisRuntimeOptions options, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? []
        });

        return builder.BuildOrbisWebApplication(options);
    }

    public static IDictionary<string, string?> ReadEnvironment(IDictionary? environment = null)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment ?? Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }
}
