using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Orbis.Stream.Core.I18n;

/// <summary>
/// Interface strings of the Razor pages, ported from the translation files of the React build
/// and embedded in the assembly (<c>Ui/ui.&lt;lang&gt;.json</c>). The language follows the
/// <see cref="Localizer"/>, so the pages and the service messages always agree.
/// </summary>
public sealed class UiText
{
    public const string FallbackLanguage = "en";

    /// <summary>
    /// Languages offered by the selector, in display order: the code of the bundle, the name in
    /// its own language, and the flag drawn beside it. The flag is a real picture shipped under
    /// <c>wwwroot/flags</c> rather than an emoji, which the platform is free not to draw at all
    /// and which a font swap turns into a pair of letters.
    /// </summary>
    public static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("it", "Italiano", "it.svg"),
        new("en", "English", "gb.svg"),
        new("de", "Deutsch", "de.svg"),
        new("es", "Español", "es.svg"),
        new("fr", "Français", "fr.svg"),
        new("pt", "Português", "pt.svg"),
        new("ru", "Русский", "ru.svg"),
        new("zh", "中文", "cn.svg"),
        new("ko", "한국어", "kr.svg"),
        new("ja", "日本語", "jp.svg")
    ];

    /// <summary>One row of the language selector: the bundle it picks, the name to show, the flag.</summary>
    /// <param name="Code">Language code, the name of the <c>ui.&lt;code&gt;.json</c> bundle.</param>
    /// <param name="Name">The language in its own language, never translated.</param>
    /// <param name="Flag">File name of the flag under <c>wwwroot/flags</c>.</param>
    public sealed record LanguageOption(string Code, string Name, string Flag);

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Bundles = Load();

    private readonly Localizer _localizer;

    public UiText(Localizer localizer)
    {
        _localizer = localizer;
    }

    public string Language
    {
        get
        {
            var language = _localizer.CurrentLanguage;
            return language is not null && Bundles.ContainsKey(language) ? language : FallbackLanguage;
        }
    }

    public string this[string key] =>
        Bundles[Language].TryGetValue(key, out var text) || Bundles[FallbackLanguage].TryGetValue(key, out text)
            ? text
            : key;

    public static bool IsSupported(string? language) => language is not null && Bundles.ContainsKey(language);

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(UiText).Assembly;
        var bundles = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            using var stream = assembly.GetManifestResourceStream($"Ui.ui.{language.Code}.json")
                ?? throw new InvalidOperationException($"Missing embedded resource Ui.ui.{language.Code}.json");
            bundles[language.Code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.ToFrozenDictionary();
        }

        return bundles.ToFrozenDictionary();
    }
}
