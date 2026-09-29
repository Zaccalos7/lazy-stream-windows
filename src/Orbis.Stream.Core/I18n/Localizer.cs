using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Orbis.Stream.Core.I18n;

/// <summary>
/// Port of <c>LoggerMessageComponent</c> + <c>LocaleChangeInterceptor</c>: resolves the request
/// language (<c>?lang=</c> first, then the language chosen in the pages (cookie), then <c>Accept-Language</c>, then the Spring default <c>en</c>)
/// and formats message codes.
/// </summary>
public sealed class Localizer
{
    public const string LanguageQueryParameter = "lang";

    /// <summary>Written by the language selector of the Razor pages.</summary>
    public const string LanguageCookie = "orbis-lang";

    private readonly MessageCatalog _catalog;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public Localizer(MessageCatalog catalog, IHttpContextAccessor httpContextAccessor)
    {
        _catalog = catalog;
        _httpContextAccessor = httpContextAccessor;
    }

    public string? CurrentLanguage
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            if (context.Request.Query.TryGetValue(LanguageQueryParameter, out var requested)
                && !string.IsNullOrWhiteSpace(requested))
            {
                return _catalog.ResolveLanguage(requested);
            }

            if (context.Request.Cookies.TryGetValue(LanguageCookie, out var chosen) && !string.IsNullOrWhiteSpace(chosen))
            {
                return _catalog.ResolveLanguage(chosen);
            }

            var acceptLanguage = context.Request.Headers.AcceptLanguage.FirstOrDefault();
            return string.IsNullOrWhiteSpace(acceptLanguage) ? null : _catalog.ResolveLanguage(acceptLanguage);
        }
    }

    public CultureInfo CurrentCulture =>
        CurrentLanguage is null ? CultureInfo.GetCultureInfo(MessageCatalog.DefaultLanguage) : _catalog.ResolveCulture(CurrentLanguage);

    public string PrintMessage(string code) => PrintMessage(code, null);

    public string PrintMessage(string code, object?[]? arguments) =>
        _catalog.GetMessage(CurrentLanguage, code, arguments);

    public string PrintMessage(CultureInfo? culture, string code, object?[]? arguments) =>
        _catalog.GetMessage(culture, code, arguments);
}
