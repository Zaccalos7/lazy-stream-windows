using System.Collections.Frozen;
using System.Text.Json;

namespace Orbis.Stream.Core.I18n;

/// <summary>
/// One page of the user guide: the title of the feature, what it does, the steps to follow, and the
/// picture that shows it (the name of the bundle of images the language draws).
/// </summary>
/// <param name="Id">Stable key of the section, the same in every language.</param>
/// <param name="Title">Name of the feature.</param>
/// <param name="Body">What the feature does, in one or two sentences.</param>
/// <param name="Steps">The steps to follow, in order, or an empty list.</param>
/// <param name="Figure">Name of the screenshot under <c>wwwroot/manual/&lt;lang&gt;/</c>, or null.</param>
/// <param name="Callout">Caption of the picture, naming the button the arrow points at.</param>
public sealed record ManualSection(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<string> Steps,
    string? Figure,
    string? Callout);

/// <summary>
/// The text of the user guide, embedded per language (<c>Ui/manual.&lt;lang&gt;.json</c>). The
/// language follows the one of the pages; a section a language is missing is shown in English, and
/// the order of the sections is always the one of the English bundle, so the guide reads the same
/// way everywhere. The pictures travel with the language too, under <c>manual/&lt;lang&gt;/</c>.
/// </summary>
public sealed class ManualText
{
    private ManualText() { }

    public static ManualText Instance { get; } = new();

    private static readonly FrozenDictionary<string, IReadOnlyList<ManualSection>> Bundles = Load();

    /// <summary>
    /// The sections of the guide for a language, in the order of the English bundle. Sections the
    /// language does not carry fall back to English rather than leaving a hole.
    /// </summary>
    public IReadOnlyList<ManualSection> Sections(string language)
    {
        var english = Bundles[UiText.FallbackLanguage];
        if (language == UiText.FallbackLanguage || !Bundles.TryGetValue(language, out var translated))
        {
            return english;
        }

        var byId = translated.ToDictionary(section => section.Id, StringComparer.Ordinal);
        return english.Select(section => byId.TryGetValue(section.Id, out var local) ? local : section).ToList();
    }

    private static FrozenDictionary<string, IReadOnlyList<ManualSection>> Load()
    {
        var assembly = typeof(ManualText).Assembly;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var bundles = new Dictionary<string, IReadOnlyList<ManualSection>>(StringComparer.Ordinal);

        foreach (var language in UiText.Languages)
        {
            using var stream = assembly.GetManifestResourceStream($"Ui.manual.{language.Code}.json");
            if (stream is null)
            {
                continue;
            }

            var document = JsonSerializer.Deserialize<ManualDocument>(stream, options)
                ?? throw new InvalidOperationException($"Empty manual bundle Ui.manual.{language.Code}.json");
            var sections = document.Sections
                ?? throw new InvalidOperationException($"Manual bundle Ui.manual.{language.Code}.json carries no sections");
            bundles[language.Code] = sections
                .Select(section => new ManualSection(
                    section.Id,
                    section.Title,
                    section.Body,
                    section.Steps ?? [],
                    section.Figure,
                    section.Callout))
                .ToList();
        }

        if (!bundles.ContainsKey(UiText.FallbackLanguage))
        {
            throw new InvalidOperationException("Missing embedded resource Ui.manual.en.json");
        }

        return bundles.ToFrozenDictionary();
    }

    private sealed record ManualDocument(List<ManualSectionDto>? Sections);

    private sealed record ManualSectionDto(
        string Id,
        string Title,
        string Body,
        List<string>? Steps,
        string? Figure,
        string? Callout);
}
