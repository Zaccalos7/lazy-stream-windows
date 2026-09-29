using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Orbis.Stream.Core.I18n;

/// <summary>Thrown when a message code has no entry in the resolved bundle.</summary>
public sealed class MessageNotFoundException : Exception
{
    public MessageNotFoundException(string code, string language)
        : base($"No message found under code '{code}' for language '{language}'.")
    {
        Code = code;
        Language = language;
    }

    public string Code { get; }

    public string Language { get; }
}

/// <summary>
/// Port of <c>MessagesConfiguration</c>: a <c>classpath:messages</c> resource bundle resolved
/// per language, always formatted with <c>MessageFormat</c>.
/// </summary>
public sealed class MessageCatalog
{
    public const string DefaultLanguage = "en";

    private static readonly string[] KnownLanguages = ["en", "it", "de", "es", "fr", "ru", "zh"];

    private readonly Dictionary<string, PropertiesBundle> _bundles;
    private readonly ILogger<MessageCatalog> _logger;

    public MessageCatalog(string messagesDirectory, ILogger<MessageCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messagesDirectory);
        _logger = logger;

        _bundles = new Dictionary<string, PropertiesBundle>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(messagesDirectory))
        {
            _logger.LogWarning("Messages directory {Directory} not found: messages fall back to the code", messagesDirectory);
            return;
        }

        foreach (var language in KnownLanguages)
        {
            var path = Path.Combine(messagesDirectory, $"messages_{language}.properties");
            if (File.Exists(path))
            {
                _bundles[language] = PropertiesBundle.Load(path);
            }
        }

        _logger.LogInformation("Loaded message bundles: {Languages}", string.Join(", ", _bundles.Keys));
    }

    public IReadOnlyCollection<string> SupportedLanguages => _bundles.Keys;

    /// <summary>Maps "it-IT", "IT_it", "zh-CN"... to a bundle key.</summary>
    public string ResolveLanguage(string? requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return DefaultLanguage;
        }

        var normalized = requestedLanguage.Replace('-', '_').Trim();
        if (_bundles.ContainsKey(normalized))
        {
            return normalized.ToLowerInvariant();
        }

        var primary = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(primary) && _bundles.ContainsKey(primary))
        {
            return primary.ToLowerInvariant();
        }

        return _bundles.ContainsKey(DefaultLanguage) ? DefaultLanguage : normalized.ToLowerInvariant();
    }

    public CultureInfo ResolveCulture(string? requestedLanguage)
    {
        var language = ResolveLanguage(requestedLanguage);
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    public string GetMessage(string? requestedLanguage, string code, params object?[]? arguments)
    {
        var language = ResolveLanguage(requestedLanguage);
        arguments ??= [];

        if (_bundles.TryGetValue(language, out var bundle) && bundle.TryGetValue(code, out var pattern))
        {
            return MessageFormatter.Format(pattern, arguments);
        }

        if (language != DefaultLanguage
            && _bundles.TryGetValue(DefaultLanguage, out var fallback)
            && fallback.TryGetValue(code, out var fallbackPattern))
        {
            return MessageFormatter.Format(fallbackPattern, arguments);
        }

        throw new MessageNotFoundException(code, language);
    }

    public string GetMessage(CultureInfo? culture, string code, params object?[]? arguments) =>
        GetMessage(culture?.Name ?? DefaultLanguage, code, arguments);

    /// <summary>Resolves the bundle directory: output folder first, then the content root.</summary>
    public static string? LocateMessagesDirectory(string? explicitPath = null, string? contentRoot = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath!);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Messages"));

        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            candidates.Add(Path.Combine(contentRoot!, "Messages"));
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
