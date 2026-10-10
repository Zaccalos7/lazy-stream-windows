using System.Text.Json;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Tests;

/// <summary>
/// The user guide is embedded per language. The pictures follow the language, so a bundle left
/// half done would show a section of English text above a picture of the English app: every
/// language must carry the same sections, in the same order, with the same figures as English.
/// </summary>
public sealed class ManualTextTests
{
    private static JsonElement Bundle(string code)
    {
        using var stream = typeof(ManualText).Assembly.GetManifestResourceStream($"Ui.manual.{code}.json")
            ?? throw new InvalidOperationException($"Missing embedded resource Ui.manual.{code}.json");
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static string[] Ids(JsonElement bundle) =>
        bundle.GetProperty("sections").EnumerateArray()
            .Select(section => section.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();

    [Fact]
    public void EveryLanguageHasItsOwnBundle()
    {
        foreach (var language in UiText.Languages)
        {
            var bundle = Bundle(language.Code);
            Assert.True(bundle.GetProperty("sections").GetArrayLength() > 0,
                $"Ui.manual.{language.Code}.json carries no sections");
        }
    }

    [Fact]
    public void EveryBundleCarriesTheSameSectionsInTheSameOrderAsEnglish()
    {
        var english = Ids(Bundle(UiText.FallbackLanguage));

        foreach (var language in UiText.Languages)
        {
            Assert.Equal(english, Ids(Bundle(language.Code)));
        }
    }

    [Fact]
    public void EverySectionKeepsItsFigureAndItsSteps()
    {
        var english = Bundle(UiText.FallbackLanguage).GetProperty("sections").EnumerateArray()
            .ToDictionary(section => section.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var language in UiText.Languages.Where(entry => entry.Code != UiText.FallbackLanguage))
        {
            foreach (var section in Bundle(language.Code).GetProperty("sections").EnumerateArray())
            {
                var id = section.GetProperty("id").GetString()!;
                var expected = english[id];
                Assert.Equal(
                    expected.GetProperty("figure").GetString(),
                    section.GetProperty("figure").GetString());
                Assert.Equal(
                    expected.GetProperty("steps").GetArrayLength(),
                    section.GetProperty("steps").GetArrayLength());
            }
        }
    }

    [Fact]
    public void NoSectionIsLeftInEnglishInAnotherLanguage()
    {
        var english = Bundle(UiText.FallbackLanguage).GetProperty("sections").EnumerateArray()
            .ToDictionary(section => section.GetProperty("id").GetString()!,
                section => section.GetProperty("title").GetString()!, StringComparer.Ordinal);

        foreach (var language in UiText.Languages.Where(entry => entry.Code != UiText.FallbackLanguage))
        {
            var translated = Bundle(language.Code).GetProperty("sections").EnumerateArray()
                .Count(section => section.GetProperty("title").GetString() != english[section.GetProperty("id").GetString()!]);

            Assert.True(translated > 0,
                $"Ui.manual.{language.Code}.json is the English guide word for word");
        }
    }
}
