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

    /// <summary>Languages offered by the selector, in display order, with their native names.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("it", "🇮🇹 Italiano"),
        ("en", "🇬🇧 English"),
        ("de", "🇩🇪 Deutsch"),
        ("es", "🇪🇸 Español"),
        ("fr", "🇫🇷 Français"),
        ("pt", "🇵🇹 Português"),
        ("ru", "🇷🇺 Русский"),
        ("zh", "🇨🇳 中文"),
        ("ko", "🇰🇷 한국어"),
        ("ja", "🇯🇵 日本語"),
        ("tlh", "🖖 tlhIngan Hol"),
        ("hi", "🇮🇳 हिन्दी"),
        ("la", "🏛️ Latina"),
        ("bn", "🇧🇩 বাংলা"),
        ("hod", "🚪 Hodor")
    ];

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
        foreach (var (code, _) in Languages)
        {
            using var stream = assembly.GetManifestResourceStream($"Ui.ui.{code}.json")
                ?? throw new InvalidOperationException($"Missing embedded resource Ui.ui.{code}.json");
            bundles[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.ToFrozenDictionary();
        }

        return bundles.ToFrozenDictionary();
    }
}
