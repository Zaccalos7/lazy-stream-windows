using System.Text.Json;
using System.Text.RegularExpressions;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Tests;

/// <summary>
/// The interface bundles are embedded, so they cannot be read off disk like the message ones: the
/// same ground is covered here, plus the flags the language selector draws. A language added by
/// dropping a file in the folder is exactly how three bundles came to be a copy of the English one
/// without a single test noticing.
/// </summary>
public sealed class UiTextTests
{
    private static Dictionary<string, string> Bundle(string code)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"Ui.ui.{code}.json")
            ?? throw new InvalidOperationException($"Missing embedded resource Ui.ui.{code}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    // The parameters a string carries, sorted: where they sit in the sentence is up to the
    // language, but which arguments it uses is not.

    private static string Placeholders(string text) =>
        string.Concat(Regex.Matches(text, @"\{\d\}").Select(match => match.Value).Order());

    [Fact]
    public void EveryBundleDefinesTheSameKeysInTheSameOrderAsEnglish()
    {
        var english = Bundle(UiText.FallbackLanguage);

        foreach (var language in UiText.Languages)
        {
            var bundle = Bundle(language.Code);
            Assert.Equal(english.Keys, bundle.Keys);
        }
    }

    [Fact]
    public void EveryBundleIsReallyTranslated()
    {
        var english = Bundle(UiText.FallbackLanguage);

        // "Video" and "URL" are the same word everywhere: what this rejects is a whole bundle
        // sitting in English, which is what Korean, Japanese and Portuguese were.
        foreach (var language in UiText.Languages.Where(entry => entry.Code != UiText.FallbackLanguage))
        {
            var bundle = Bundle(language.Code);
            var translated = english.Keys.Count(key => bundle[key] != english[key]);

            Assert.True(translated > english.Count / 2,
                $"ui.{language.Code}.json is barely translated: only {translated} of {english.Count} strings differ from English");
        }
    }

    [Fact]
    public void NoBundlePrefixesItsStringsWithALanguageTag()
    {
        foreach (var language in UiText.Languages)
        {
            foreach (var (key, text) in Bundle(language.Code))
            {
                Assert.False(Regex.IsMatch(text, @"^\s*[\[(]"),
                    $"ui.{language.Code}.json tags '{key}' with the language instead of translating it");
            }
        }
    }

    [Fact]
    public void TranslationsKeepEveryParameterOfTheEnglishString()
    {
        var english = Bundle(UiText.FallbackLanguage);

        foreach (var language in UiText.Languages)
        {
            var bundle = Bundle(language.Code);
            foreach (var (key, text) in english)
            {
                Assert.True(
                    Placeholders(text) == Placeholders(bundle[key]),
                    $"ui.{language.Code}.json does not carry the same parameters as '{key}': " +
                    $"'{text}' against '{bundle[key]}'");
            }
        }
    }

    [Fact]
    public void TheSelectorOffersExactlyTheLanguagesTheMessagesAnswerIn()
    {
        var catalog = new MessageCatalog(
            Path.Combine(AppContext.BaseDirectory, "Messages"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageCatalog>.Instance);

        Assert.Equal(
            catalog.SupportedLanguages.Order(),
            UiText.Languages.Select(language => language.Code).Order());
    }

    [Fact]
    public void EveryLanguageHasItsOwnFlagAndTheFileIsThere()
    {
        var flags = Path.Combine(AppContext.BaseDirectory, "flags");

        foreach (var language in UiText.Languages)
        {
            Assert.EndsWith(".svg", language.Flag, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                File.Exists(Path.Combine(flags, language.Flag)),
                $"the flag of '{language.Code}' ({language.Flag}) is missing: the selector would draw a broken image");
        }

        // Two languages sharing one flag is a copy-paste that has not been looked at.
        Assert.Equal(UiText.Languages.Count, UiText.Languages.Select(language => language.Flag).Distinct().Count());
    }
}