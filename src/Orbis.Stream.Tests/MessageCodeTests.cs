using System.Text.RegularExpressions;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Tests;

/// <summary>
/// A message code is a string, so nothing stops the code from asking for one the bundles do not
/// define, and the answer is a 500 at the moment a user triggers it. This reads the sources and
/// checks every literal that is passed where a code is expected against the English bundle, which
/// is the one every other bundle falls back to.
/// </summary>
public sealed class MessageCodeTests
{
    /// <summary>The places a message code is written as the first argument.</summary>
    private static readonly Regex CodeLiterals = new(
        @"\b(?:PrintMessage|Build|BuildBadResponse|NotFoundCustomException|DuplicationEntityException|LiveException|FileReadingException|StreamingException|SqlCustomException)\s*\(\s*""([a-z][a-z0-9]*(?:\.[a-z0-9]+)+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryMessageCodeTheSourcesUseIsDefined()
    {
        var sources = SourcesOf("Orbis.Stream.Core");
        Assert.NotNull(sources);
        if (sources is null)
        {
            // A published test run has no sources to read: nothing to check, and nothing to fail on.
            return;
        }

        var bundle = PropertiesBundle.Parse(File.ReadAllText(EnglishPath()));
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in CodeLiterals.Matches(File.ReadAllText(file)))
            {
                var code = match.Groups[1].Value;
                if (!bundle.TryGetValue(code, out _))
                {
                    missing.Add(code);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "the English bundle has no code for: " + string.Join(", ", missing));
    }

    private static string EnglishPath() =>
        Path.Combine(AppContext.BaseDirectory, "Messages", "messages_en.properties");

    /// <summary>The source folder of a project, found by walking up to the solution; null when absent.</summary>
    private static string? SourcesOf(string project)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "OrbisStream.slnx")))
            {
                continue;
            }

            var sources = Path.Combine(directory.FullName, "src", project);
            return Directory.Exists(sources) ? sources : null;
        }

        return null;
    }
}
