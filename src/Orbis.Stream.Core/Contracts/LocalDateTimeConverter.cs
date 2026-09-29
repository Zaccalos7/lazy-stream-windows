using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbis.Stream.Core.Contracts;

/// <summary>
/// Serializes <see cref="DateTime"/> the way Jackson serialized <c>LocalDateTime</c>:
/// ISO-8601 without timezone and without a trailing "Z".
/// </summary>
public sealed class LocalDateTimeConverter : JsonConverter<DateTime>
{
    public const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fff";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return DateTimeParsing.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(Pattern, CultureInfo.InvariantCulture));
    }
}

public sealed class NullableLocalDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeParsing.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString(LocalDateTimeConverter.Pattern, CultureInfo.InvariantCulture));
    }
}

public static class DateTimeParsing
{
    private static readonly string[] SupportedPatterns =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd"
    ];

    /// <summary>Accepts both the Jackson format and the SQLite/Hibernate storage format.</summary>
    public static DateTime Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        return default;
    }
}
