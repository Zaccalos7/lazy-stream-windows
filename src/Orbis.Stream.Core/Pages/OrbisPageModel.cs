using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Pages;

/// <summary>Kind of the InfoBar shown at the top of a page after an action.</summary>
public enum NoticeKind
{
    Success,
    Warning,
    Error
}

/// <summary>
/// Base of every page: the handlers call the services in-process (no HTTP round trip like the
/// React client did) and turn their outcome, or the exception they throw, into the localized
/// InfoBar shown after the Post/Redirect/Get.
/// </summary>
public abstract class OrbisPageModel : PageModel
{
    [TempData]
    public string? NoticeText { get; set; }

    [TempData]
    public string? NoticeKindName { get; set; }

    public NoticeKind? Notice => Enum.TryParse<NoticeKind>(NoticeKindName, out var kind) ? kind : null;

    protected void SetNotice(NoticeKind kind, string text)
    {
        NoticeKindName = kind.ToString();
        NoticeText = text;
    }

    /// <summary>Runs a service operation and records its <c>{response, message}</c> as the notice.</summary>
    protected bool Run(Func<MessageResponse> operation)
    {
        return Try(() =>
        {
            var result = operation();
            var success = result.Body.Response == ApiEnvelope.Success;
            SetNotice(success ? NoticeKind.Success : NoticeKind.Error, result.Body.Message);
            return success;
        });
    }

    /// <summary>Runs an operation and records the localized message of the exception it throws, if any.</summary>
    protected bool Try(Func<bool> operation)
    {
        try
        {
            return operation();
        }
        catch (RequestValidationException exception)
        {
            SetNotice(NoticeKind.Error, string.Join(Environment.NewLine, exception.FieldErrors.Values));
        }
        catch (DuplicationEntityException exception)
        {
            SetNotice(NoticeKind.Warning, Message(exception));
        }
        catch (OrbisException exception)
        {
            SetNotice(NoticeKind.Error, Message(exception));
        }
        catch (SqliteException exception)
        {
            SetNotice(NoticeKind.Error, exception.Message);
        }

        return false;
    }

    /// <summary>Redirects to the same page keeping the query string (filters, paging).</summary>
    protected RedirectResult BackToPage() => Redirect(Request.Path + Request.QueryString.ToString()
        .Replace("handler=", "_=", StringComparison.Ordinal));

    private string Message(OrbisException exception)
    {
        var localizer = HttpContext.RequestServices.GetService(typeof(Localizer)) as Localizer;
        try
        {
            return localizer?.PrintMessage(exception.MessageCode, exception.Parameters) ?? exception.MessageCode;
        }
        catch (MessageNotFoundException)
        {
            return exception.MessageCode;
        }
    }
}
