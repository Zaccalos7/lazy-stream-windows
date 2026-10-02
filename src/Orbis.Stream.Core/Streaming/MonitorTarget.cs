using System.Globalization;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// How a monitor travels as a gdigrab target: <c>monitor:LEFT,TOP,WIDTHxHEIGHT</c>. The rectangle
/// is in desktop coordinates, which is what <c>-offset_x</c> and <c>-offset_y</c> expect, and it is
/// written once when the monitor is listed so the command line never has to ask Windows again.
/// </summary>
public static class MonitorTarget
{
    public const string Desktop = "desktop";

    private const string Prefix = "monitor:";

    public static string Of(int left, int top, int width, int height) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{left},{top},{width}x{height}");

    public static bool TryParse(string? target, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (target is null || !target.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = target[Prefix.Length..].Split(',', 'x');
        return parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out left)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out top)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            && width > 0
            && height > 0;
    }
}
