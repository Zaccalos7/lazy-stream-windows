using System.Text.RegularExpressions;

namespace Orbis.Stream.Tests;

/// <summary>
/// The stylesheets are one concern each: the shared seven travel with every page, and a sheet of a
/// single page travels with that page alone. These tests are the contract between the two: a page
/// that draws something must link the sheet that styles it, a sheet nobody links is a sheet
/// nobody gets, and no sheet may name a custom property it never defines.
/// </summary>
public sealed partial class StyleSheetTests : IClassFixture<ApplicationFixture>
{
    /// <summary>The sheets the layout links, in the order it links them.</summary>
    private static readonly string[] Shared = ["tokens", "base", "layout", "controls", "surfaces", "overlays", "feedback"];

    /// <summary>A sheet of a single page, and the pages that draw it.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Owned =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["composer"] = ["/orbis/mainLive", "/orbis/mainLayout"],
            ["preview"] = ["/orbis/mainPreview"],
            ["meters"] = ["/orbis/mainTaskManager"],
            ["cleanup"] = ["/orbis/mainLiveHistory"],
            ["dashboard"] = ["/orbis/mainMenu", "/orbis/mainLiveMenu"]
        };

    private readonly ApplicationFixture _fixture;

    public StyleSheetTests(ApplicationFixture fixture) => _fixture = fixture;

    private static string SheetDirectory => Path.Combine(AppContext.BaseDirectory, "css");

    private static string[] SheetsOnDisk() => Directory.GetFiles(SheetDirectory, "*.css")
        .Select(Path.GetFileNameWithoutExtension)
        .Select(name => name!)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private async Task<string[]> SheetsOfPageAsync(string page)
    {
        using var response = await _fixture.Client.GetAsync(page);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        return LinkPattern().Matches(html).Select(match => match.Groups[2].Value).Distinct().ToArray();
    }

    [Theory]
    [InlineData("/orbis/mainMenu")]
    [InlineData("/orbis/mainLiveMenu")]
    [InlineData("/orbis/mainLive")]
    [InlineData("/orbis/mainLiveHistory")]
    [InlineData("/orbis/mainLayout")]
    [InlineData("/orbis/mainSetting")]
    [InlineData("/orbis/mainTaskManager")]
    public async Task Every_page_carries_the_shared_sheets_and_nothing_missing(string page)
    {
        var linked = await SheetsOfPageAsync( page);
        var onDisk = SheetsOnDisk();

        foreach (var sheet in Shared)
        {
            Assert.Contains(sheet, linked);
        }

        Assert.All(linked, name => Assert.Contains(name, onDisk));
    }

    /// <summary>
    /// A sheet that only one page draws is that page's to link, and the page that draws it must say
    /// so: the composer without its sheet is a canvas with no tiles on it.
    /// </summary>
    [Theory]
    [InlineData("/orbis/mainLive")]
    [InlineData("/orbis/mainLayout")]
    [InlineData("/orbis/mainPreview")]
    [InlineData("/orbis/mainTaskManager")]
    [InlineData("/orbis/mainLiveHistory")]
    [InlineData("/orbis/mainMenu")]
    [InlineData("/orbis/mainLiveMenu")]
    public async Task A_page_links_the_sheets_it_draws_and_only_those(string page)
    {
        var linked = await SheetsOfPageAsync( page);

        foreach (var (sheet, pages) in Owned)
        {
            var belongsToThisPage = pages.Contains(page, StringComparer.OrdinalIgnoreCase);
            if (belongsToThisPage)
            {
                Assert.Contains(sheet, linked);
            }
            else
            {
                Assert.DoesNotContain(sheet, linked);
            }
        }
    }

    /// <summary>
    /// Every sheet is linked by something. A sheet no page links is dead weight shipped to every
    /// installation and styling nothing.
    /// </summary>
    [Fact]
    public async Task Every_sheet_is_linked_by_at_least_one_page()
    {
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in Owned.Values.SelectMany(pages => pages).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            linked.UnionWith(await SheetsOfPageAsync( page));
        }

        // The countdown stands outside the shell and carries the three it needs on its own.
        linked.UnionWith(await SheetsOfPageAsync( "/orbis/countdown"));

        foreach (var sheet in SheetsOnDisk())
        {
            Assert.Contains(sheet, linked);
        }
    }

    /// <summary>
    /// A sheet that reads a custom property no sheet defines falls back to what the browser has:
    /// the declaration is dropped and the thing it was meant to colour quietly keeps another one.
    /// </summary>
    [Fact]
    public void No_sheet_names_a_custom_property_that_no_sheet_defines()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var read = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var path in Directory.GetFiles(SheetDirectory, "*.css"))
        {
            var name = Path.GetFileName(path);
            var css = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            foreach (Match match in Regex.Matches(css, @"(--[\w-]+)\s*:"))
            {
                defined.Add(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(css, @"var\((--[\w-]+)"))
            {
                if (!read.TryGetValue(name, out var properties))
                {
                    read[name] = properties = new HashSet<string>(StringComparer.Ordinal);
                }
                properties.Add(match.Groups[1].Value);
            }
        }

        var undefined = read
            .SelectMany(entry => entry.Value.Select(property => (entry.Key, property)))
            .Where(entry => !defined.Contains(entry.property))
            .ToArray();

        Assert.Empty(undefined);
    }

    [GeneratedRegex("href=\"(/css/([\\w-]+)\\.css)\"")]
    private static partial Regex LinkPattern();
}