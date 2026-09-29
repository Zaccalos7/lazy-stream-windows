using Microsoft.AspNetCore.Http;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Http;

public sealed record MessageResponse(int StatusCode, ApiEnvelope Body);

/// <summary>
/// Port of <c>com.orbis.stream.handler.ResponseHandler</c>: every operation answers with
/// <c>{ "response": "success" | "error", "message": "&lt;localized&gt;" }</c>.
/// </summary>
public sealed class ResponseFactory
{
    private readonly Localizer _localizer;

    public ResponseFactory(Localizer localizer)
    {
        _localizer = localizer;
    }

    public MessageResponse Build(string code, int statusCode, object?[]? parameters = null)
    {
        var localizedMessage = _localizer.PrintMessage(code, parameters);
        var outcome = statusCode is >= 200 and < 300 ? ApiEnvelope.Success : ApiEnvelope.Error;
        return new MessageResponse(statusCode, new ApiEnvelope(outcome, localizedMessage));
    }

    public MessageResponse Build(string code, int statusCode, object? parameter) =>
        Build(code, statusCode, [parameter]);

    /// <summary>Same as <see cref="Build(string, int, object?[])"/> with the Spring default of HTTP 500.</summary>
    public MessageResponse BuildBadResponse(string code, object?[]? parameters = null) =>
        Build(code, StatusCodes.Status500InternalServerError, parameters);

    /// <summary>Port of <c>buildBadResponseWithoutMessageLabel</c>, used for errors whose text is already final.</summary>
    public MessageResponse BuildBadResponseWithoutMessageLabel(string message) =>
        new(StatusCodes.Status500InternalServerError, new ApiEnvelope(ApiEnvelope.Error, message));
}
