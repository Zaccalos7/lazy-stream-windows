using System.Reflection;

namespace Orbis.Stream.Core.SystemInfo;

/// <summary>
/// Which build of the application is running and who wrote it. Read from the attributes the build
/// stamps on the assembly out of <c>Directory.Build.props</c>, so the number on the page and the one
/// in the setup are the same one and neither is written twice.
/// <para>The author is the <c>Company</c> of the build: it is the only one of the attributes that
/// says who made the thing rather than what it is called.</para>
/// </summary>
public sealed record AppInfo(string Name, string Version, string Author)
{
    /// <summary>The build that is running: read once, it cannot change under the page that shows it.</summary>
    public static AppInfo Current { get; } = Read();

    /// <summary>Name and version on one line, for the title of the About dialog.</summary>
    public string NameAndVersion => $"{Name} {Version}";

    private static AppInfo Read()
    {
        var assembly = typeof(AppInfo).Assembly;

        return new AppInfo(
            Of<AssemblyProductAttribute>(assembly)?.Product ?? "Orbis Stream",
            VersionOf(assembly),
            Of<AssemblyCompanyAttribute>(assembly)?.Company ?? string.Empty);
    }

    private static T? Of<T>(Assembly assembly) where T : Attribute =>
        assembly.GetCustomAttribute<T>();

    /// <summary>
    /// The version of the build, without the commit the source link appends to it: a build stamped
    /// "2.0.12+9f1c0ab" is version 2.0.12 as far as a person reading the sidebar is concerned.
    /// </summary>
    private static string VersionOf(Assembly assembly)
    {
        var informational = Of<AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        var version = informational is null ? null : informational.Split('+')[0];

        return !string.IsNullOrWhiteSpace(version)
            ? version
            : assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
