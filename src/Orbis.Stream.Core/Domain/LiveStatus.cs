using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbis.Stream.Core.Domain;

/// <summary>
/// Port of <c>com.orbis.stream.enums.LiveStatusEnum</c>. The declaration order is
/// meaningful: the Java entity stored the enum ordinal, so the values are kept aligned
/// to stay compatible with databases created by the previous version.
/// </summary>
[JsonConverter(typeof(LiveStatusJsonConverter))]
public enum LiveStatus
{
    Live = 0,
    Offline = 1,
    Ended = 2,
    Error = 3,
    Stopped = 4
}

public static class LiveStatusExtensions
{
    /// <summary>Wire/database name, e.g. <c>LIVE</c>.</summary>
    public static string ToWireValue(this LiveStatus status) => status switch
    {
        LiveStatus.Live => "LIVE",
        LiveStatus.Offline => "OFFLINE",
        LiveStatus.Ended => "ENDED",
        LiveStatus.Error => "ERROR",
        LiveStatus.Stopped => "STOPPED",
        _ => status.ToString().ToUpperInvariant()
    };

    public static bool TryParseWireValue(string? value, out LiveStatus status)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "LIVE":
                status = LiveStatus.Live;
                return true;
            case "OFFLINE":
                status = LiveStatus.Offline;
                return true;
            case "ENDED":
                status = LiveStatus.Ended;
                return true;
            case "ERROR":
                status = LiveStatus.Error;
                return true;
            case "STOPPED":
                status = LiveStatus.Stopped;
                return true;
            default:
                status = LiveStatus.Offline;
                return false;
        }
    }

    /// <summary>Reads the column value written by Hibernate (ordinal integer, text also accepted).</summary>
    public static LiveStatus FromStorage(object? value)
    {
        return value switch
        {
            null => LiveStatus.Offline,
            LiveStatus status => status,
            int ordinal when ordinal >= 0 && ordinal <= (int)LiveStatus.Stopped => (LiveStatus)ordinal,
            long ordinal when ordinal >= 0 && ordinal <= (int)LiveStatus.Stopped => (LiveStatus)(int)ordinal,
            string text when int.TryParse(text, out var parsedOrdinal)
                && parsedOrdinal >= 0
                && parsedOrdinal <= (int)LiveStatus.Stopped => (LiveStatus)parsedOrdinal,
            string text => TryParseWireValue(text, out var parsed) ? parsed : LiveStatus.Offline,
            _ => LiveStatus.Offline
        };
    }

    public static int ToStorageValue(this LiveStatus status) => (int)status;
}

public sealed class LiveStatusJsonConverter : JsonConverter<LiveStatus>
{
    public override LiveStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return LiveStatusExtensions.FromStorage(reader.GetInt32());
        }

        var value = reader.GetString();
        return LiveStatusExtensions.FromStorage(value);
    }

    public override void Write(Utf8JsonWriter writer, LiveStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireValue());
    }
}
