namespace Orbis.Stream.Core.Http;

/// <summary>Base class of the application exceptions: they carry a message code plus MessageFormat parameters.</summary>
public abstract class OrbisException : Exception
{
    protected OrbisException(string messageCode, object?[]? parameters = null, Exception? innerException = null)
        : base(messageCode, innerException)
    {
        MessageCode = messageCode;
        Parameters = parameters;
    }

    public string MessageCode { get; }

    public object?[]? Parameters { get; }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.NotFoundCustomException</c> (HTTP 404).</summary>
public sealed class NotFoundCustomException : OrbisException
{
    public NotFoundCustomException(string messageCode, object?[]? parameters = null)
        : base(messageCode, parameters)
    {
    }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.DuplicationEntityException</c> (HTTP 409).</summary>
public sealed class DuplicationEntityException : OrbisException
{
    public DuplicationEntityException(string messageCode, object?[]? parameters = null)
        : base(messageCode, parameters)
    {
    }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.LiveException</c> (HTTP 409).</summary>
public sealed class LiveException : OrbisException
{
    public LiveException(string messageCode, object?[]? parameters = null)
        : base(messageCode, parameters)
    {
    }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.FileReadingException</c> (HTTP 500).</summary>
public sealed class FileReadingException : OrbisException
{
    public FileReadingException(string messageCode, object?[]? parameters = null, Exception? innerException = null)
        : base(messageCode, parameters, innerException)
    {
    }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.StreamingException</c> (HTTP 500).</summary>
public sealed class StreamingException : OrbisException
{
    public StreamingException(string messageCode, object?[]? parameters = null)
        : base(messageCode, parameters)
    {
    }
}

/// <summary>Port of <c>com.orbis.stream.exceptions.SQLCustomException</c> (HTTP 500).</summary>
public sealed class SqlCustomException : OrbisException
{
    public SqlCustomException(string messageCode, object?[]? parameters = null, Exception? innerException = null)
        : base(messageCode, parameters, innerException)
    {
    }
}

/// <summary>Port of the <c>MethodArgumentNotValidException</c> handler: one localized entry per invalid field.</summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string> fieldErrors)
        : base("Validation failed")
    {
        FieldErrors = fieldErrors;
    }

    public IReadOnlyDictionary<string, string> FieldErrors { get; }
}
