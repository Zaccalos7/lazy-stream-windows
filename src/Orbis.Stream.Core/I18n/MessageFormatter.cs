using System.Globalization;
using System.Text;

namespace Orbis.Stream.Core.I18n;

/// <summary>
/// Subset of <c>java.text.MessageFormat</c> used by the <c>messages_*.properties</c> bundles:
/// <c>{0}</c> style arguments, <c>''</c> escaping and quoted literals.
/// </summary>
public static class MessageFormatter
{
    public static string Format(string pattern, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (arguments.Length == 0
            && !pattern.Contains('{', StringComparison.Ordinal)
            && !pattern.Contains('\'', StringComparison.Ordinal))
        {
            return pattern;
        }

        var builder = new StringBuilder(pattern.Length + 16);
        var index = 0;

        while (index < pattern.Length)
        {
            var character = pattern[index];

            if (character == '\'')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '\'')
                {
                    builder.Append('\'');
                    index += 2;
                    continue;
                }

                // Quoted literal: copy verbatim until the closing quote.
                index++;
                while (index < pattern.Length)
                {
                    if (pattern[index] == '\'')
                    {
                        if (index + 1 < pattern.Length && pattern[index + 1] == '\'')
                        {
                            builder.Append('\'');
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    builder.Append(pattern[index]);
                    index++;
                }

                continue;
            }

            if (character == '{')
            {
                var closing = pattern.IndexOf('}', index);
                if (closing < 0)
                {
                    builder.Append(pattern, index, pattern.Length - index);
                    break;
                }

                var body = pattern[(index + 1)..closing];
                var separator = body.IndexOf(',', StringComparison.Ordinal);
                var numberText = separator < 0 ? body : body[..separator];
                var formatType = separator < 0 ? null : body[(separator + 1)..];

                if (int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var argumentIndex))
                {
                    if (argumentIndex >= 0 && argumentIndex < arguments.Length)
                    {
                        builder.Append(FormatArgument(arguments[argumentIndex], formatType));
                        index = closing + 1;
                        continue;
                    }

                    builder.Append('{').Append(numberText);
                    if (separator >= 0)
                    {
                        builder.Append(',').Append(formatType);
                    }

                    builder.Append('}');
                    index = closing + 1;
                    continue;
                }

                builder.Append(pattern, index, closing - index + 1);
                index = closing + 1;
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    private static string FormatArgument(object? argument, string? formatType)
    {
        if (string.IsNullOrWhiteSpace(formatType))
        {
            return Convert.ToString(argument, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (argument is IFormattable formattable)
        {
            return formatType switch
            {
                "number" => formattable.ToString("#,##0.###", CultureInfo.CurrentCulture),
                "currency" => formattable.ToString("C", CultureInfo.CurrentCulture),
                "percent" => formattable.ToString("P", CultureInfo.CurrentCulture),
                "date" => formattable.ToString("d", CultureInfo.CurrentCulture),
                "time" => formattable.ToString("t", CultureInfo.CurrentCulture),
                _ => Convert.ToString(formattable, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        return Convert.ToString(argument, CultureInfo.CurrentCulture) ?? string.Empty;
    }
}
