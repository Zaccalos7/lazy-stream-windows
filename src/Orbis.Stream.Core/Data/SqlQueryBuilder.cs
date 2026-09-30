using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Contracts;

namespace Orbis.Stream.Core.Data;

public enum FilterKind
{
    Text,
    Integer,
    Long,
    Boolean,
    Double,
    DateTime,
    LiveStatus
}

public sealed record FilterField(string Column, FilterKind Kind);

/// <summary>
/// Port of <c>DynamicSpecificationBuilder</c>: every request parameter is an equality filter
/// on an entity property. Properties are named exactly like the JPA attributes, because that
/// is what the compiled React build sends.
/// </summary>
public static class EntityFields
{
    public static readonly IReadOnlyDictionary<string, FilterField> Video = new Dictionary<string, FilterField>(StringComparer.Ordinal)
    {
        ["pkid"] = new FilterField("pkid", FilterKind.Integer),
        ["name"] = new FilterField("name", FilterKind.Text),
        ["videoPath"] = new FilterField("video_path", FilterKind.Text),
        ["extension"] = new FilterField("extension", FilterKind.Text),
        ["liveStatus"] = new FilterField("live_status", FilterKind.LiveStatus),
        ["lastTimeStampBeforeStop"] = new FilterField("last_time_stamp_before_stop", FilterKind.Long),
        ["message"] = new FilterField("message", FilterKind.Text),
        ["shouldBeStop"] = new FilterField("should_be_stop", FilterKind.Boolean),
        ["startDateLive"] = new FilterField("start_date_live", FilterKind.DateTime),
        ["channelName"] = new FilterField("channel_name", FilterKind.Text),
        ["videoLiveHistory"] = new FilterField("video_live_history_pkid", FilterKind.Long),
        ["videoSetting"] = new FilterField("video_setting_id", FilterKind.Integer)
    };

    public static readonly IReadOnlyDictionary<string, FilterField> Setting = new Dictionary<string, FilterField>(StringComparer.Ordinal)
    {
        ["id"] = new FilterField("id", FilterKind.Integer),
        ["streamUrl"] = new FilterField("stream_url", FilterKind.Text),
        ["streamKey"] = new FilterField("stream_key", FilterKind.Text),
        ["platformStreamName"] = new FilterField("platform_stream_name", FilterKind.Text),
        ["description"] = new FilterField("description", FilterKind.Text),
        ["videoFolder"] = new FilterField("video_folder", FilterKind.Text),
        ["isActive"] = new FilterField("is_active", FilterKind.Boolean),
        ["channelName"] = new FilterField("channel_name", FilterKind.Text),
        ["gopSize"] = new FilterField("gop_size", FilterKind.Integer),
        ["lastModified"] = new FilterField("last_modified", FilterKind.DateTime)
    };

    public static readonly IReadOnlyDictionary<string, FilterField> VideoSetting = new Dictionary<string, FilterField>(StringComparer.Ordinal)
    {
        ["id"] = new FilterField("id", FilterKind.Integer),
        ["title"] = new FilterField("title", FilterKind.Text),
        ["videoCodec"] = new FilterField("video_codec", FilterKind.Integer),
        ["videoCodecName"] = new FilterField("video_codec_name", FilterKind.Text),
        ["pixelFormat"] = new FilterField("pixel_format", FilterKind.Integer),
        ["videoBitrate"] = new FilterField("video_bitrate", FilterKind.Integer),
        ["videoFormat"] = new FilterField("video_format", FilterKind.Text),
        ["lastModified"] = new FilterField("last_modified", FilterKind.DateTime),
        ["isDefaultConfiguration"] = new FilterField("is_default_configuration", FilterKind.Boolean),
        ["defaultPlatformConfiguration"] = new FilterField("default_platform_configuration", FilterKind.Text),
        ["gopSize"] = new FilterField("gop_size", FilterKind.Integer),
        ["videoWidth"] = new FilterField("video_width", FilterKind.Integer),
        ["videoHeight"] = new FilterField("video_height", FilterKind.Integer),
        ["frameRate"] = new FilterField("frame_rate", FilterKind.Double),
        ["isVideoAndAudioSettingActive"] = new FilterField("is_video_and_audio_setting_active", FilterKind.Boolean)
    };
}

/// <summary>Raised for filter/parameter combinations the previous version answered with a 500.</summary>
public sealed class OrbisQueryException : Exception
{
    public OrbisQueryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Small fluent SQL builder: conditions, ordering and paging with bound parameters.</summary>
public sealed class SqlQueryBuilder
{
    private const string ValuePlaceholder = "{value}";

    private readonly List<string> _conditions = [];
    private readonly List<(string Name, object? Value)> _parameters = [];
    private readonly List<string> _orderBy = [];
    private int _counter;

    public string? Join { get; set; }

    public string? TableAlias { get; set; } = "t";

    public int? Limit { get; set; }

    public int? Offset { get; set; }

    public bool IsEmpty => _conditions.Count == 0;

    public IReadOnlyList<(string Name, object? Value)> Parameters => _parameters;

    public string WhereClause => _conditions.Count == 0
        ? string.Empty
        : "WHERE " + string.Join(" AND ", _conditions);

    public string OrderByClause => _orderBy.Count == 0 ? string.Empty : "ORDER BY " + string.Join(", ", _orderBy);

    public SqlQueryBuilder WhereEquals(string column, object? value)
    {
        var name = $"@p{_counter++}";
        _conditions.Add($"{column} = {name}");
        _parameters.Add((name, value));
        return this;
    }

    public SqlQueryBuilder WhereRaw(string clause, object? value)
    {
        var name = $"@p{_counter++}";
        _conditions.Add(clause.Replace(ValuePlaceholder, name, StringComparison.Ordinal));
        _parameters.Add((name, value));
        return this;
    }

    public SqlQueryBuilder WherePredicate(string clause) => WhereRaw(clause, null);

    public SqlQueryBuilder OrderBy(string column, bool descending)
    {
        _orderBy.Add($"{column} {(descending ? "DESC" : "ASC")}");
        return this;
    }

    public SqlQueryBuilder ApplyPaging(int page, int size)
    {
        Offset = page * size;
        Limit = size;
        return this;
    }

    public void ApplyTo(SqliteCommand command)
    {
        foreach (var (name, value) in _parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    public string Qualified(string column) => $"{TableAlias}.{column}";

    public string Column(FilterField field) => Qualified(field.Column);

    public bool TryResolveField(
        IReadOnlyDictionary<string, FilterField> fields,
        string propertyName,
        out FilterField field)
    {
        return fields.TryGetValue(propertyName, out field!);
    }
}

public static class FilterValueConverter
{
    public static object Convert(FilterKind kind, string? value)
    {
        var text = value?.Trim() ?? string.Empty;

        return kind switch
        {
            FilterKind.Text => value ?? string.Empty,
            FilterKind.Integer => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                ? intValue
                : throw new OrbisQueryException($"Tipo non supportato per il filtro: {text}"),
            FilterKind.Long => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                ? longValue
                : throw new OrbisQueryException($"Tipo non supportato per il filtro: {text}"),
            FilterKind.Boolean => bool.TryParse(text, out var boolValue)
                ? boolValue
                : throw new OrbisQueryException($"Tipo non supportato per il filtro: {text}"),
            FilterKind.Double => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
                ? doubleValue
                : throw new OrbisQueryException($"Tipo non supportato per il filtro: {text}"),
            FilterKind.DateTime => DateTimeParsing.Parse(text),
            FilterKind.LiveStatus => Domain.LiveStatusExtensions.FromStorage(
                Domain.LiveStatusExtensions.TryParseWireValue(text, out var status) ? status : text),
            _ => throw new OrbisQueryException($"Tipo non supportato: {kind}")
        };
    }
}
