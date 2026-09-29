using System.Globalization;
using System.Text;

namespace Orbis.Stream.Core.I18n;

/// <summary>Reads a Java <c>.properties</c> resource bundle.</summary>
public sealed class PropertiesBundle
{
    private readonly Dictionary<string, string> _values;

    private PropertiesBundle(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static PropertiesBundle Load(string path)
    {
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    public static PropertiesBundle Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var currentKey = new StringBuilder();
        var currentValue = new StringBuilder();
        var continuing = false;

        foreach (var rawLine in lines)
        {
            if (continuing)
            {
                // A continuation line is part of the value as it is, only its leading
                // whitespace is removed, and it may continue the value once again.
                var continuation = rawLine.TrimStart();
                if (EndsWithContinuation(continuation))
                {
                    currentValue.Append(continuation[..^1]);
                    continue;
                }

                currentValue.Append(continuation);
                continuing = false;
                continue;
            }

            var trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            var separator = FindSeparator(trimmed);
            if (separator < 0)
            {
                // A natural line without a separator has no key: Java ignores it.
                continue;
            }

            // A new logical line starts: the previous one (possibly multi line) is complete.
            Store(values, currentKey, currentValue);
            currentKey.Append(trimmed[..separator].Trim());
            var valuePart = trimmed[(separator + 1)..];

            if (EndsWithContinuation(valuePart))
            {
                continuing = true;
                currentValue.Append(valuePart[..^1]);
                continue;
            }

            currentValue.Append(valuePart);
            Store(values, currentKey, currentValue);
        }

        if (currentKey.Length > 0)
        {
            Store(values, currentKey, currentValue);
        }

        return new PropertiesBundle(values);
    }

    private static void Store(
        Dictionary<string, string> values,
        StringBuilder key,
        StringBuilder value)
    {
        if (key.Length == 0)
        {
            value.Clear();
            return;
        }

        values[key.ToString()] = Unescape(value.ToString().TrimStart());
        key.Clear();
        value.Clear();
    }

    private static bool EndsWithContinuation(string value) =>
        value.EndsWith('\\') && !value.EndsWith("\\\\", StringComparison.Ordinal);

    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

    public IReadOnlyDictionary<string, string> Values => _values;

    private static int FindSeparator(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character is '=' or ':')
            {
                return index;
            }

            if (char.IsWhiteSpace(character))
            {
                // A line without separator but with a key is kept as an empty value.
                return index;
            }
        }

        return -1;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                builder.Append(character);
                continue;
            }

            index++;
            var escaped = value[index];
            switch (escaped)
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'u' when index + 4 < value.Length
                              && ushort.TryParse(
                                  value.Substring(index + 1, 4),
                                  NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture,
                                  out var code):
                    builder.Append((char)code);
                    index += 4;
                    break;
                default:
                    builder.Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }
}
