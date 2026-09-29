using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Http;

/// <summary>
/// Port of <c>com.orbis.stream.handler.ExceptionsHandler</c>: maps every application exception to
/// the same status code and payload the Spring advice produced.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ResponseFactory _responseFactory;
    private readonly Localizer _localizer;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ResponseFactory responseFactory,
        Localizer localizer,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _responseFactory = responseFactory;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (RequestValidationException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, exception.FieldErrors).ConfigureAwait(false);
        }
        catch (NotFoundCustomException exception)
        {
            await WriteAsync(context, StatusCodes.Status404NotFound, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (DuplicationEntityException exception)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (LiveException exception)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (FileReadingException exception)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (StreamingException exception)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (SqlCustomException exception)
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, Build(exception.MessageCode, exception.Parameters))
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            _logger.LogError(exception, "Database error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                _responseFactory.BuildBadResponseWithoutMessageLabel(exception.Message).Body).ConfigureAwait(false);
        }
        catch (MessageNotFoundException exception)
        {
            _logger.LogError(exception, "Missing message code {Code}", exception.Code);
            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                _responseFactory.BuildBadResponseWithoutMessageLabel(exception.Message).Body).ConfigureAwait(false);
        }
        catch (OrbisQueryException exception)
        {
            _logger.LogError(exception, "Invalid query on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                _responseFactory.BuildBadResponseWithoutMessageLabel(exception.Message).Body).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            await WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                _responseFactory.BuildBadResponseWithoutMessageLabel(exception.Message).Body).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                _responseFactory.BuildBadResponseWithoutMessageLabel(exception.Message).Body).ConfigureAwait(false);
        }
    }

    private ApiEnvelope Build(string code, object?[]? parameters)
    {
        try
        {
            return _responseFactory.Build(code, StatusCodes.Status500InternalServerError, parameters).Body;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to localize message code {Code}", code);
            return new ApiEnvelope(ApiEnvelope.Error, code);
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, ApiEnvelope body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, IReadOnlyDictionary<string, string> body)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions, context.RequestAborted).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
