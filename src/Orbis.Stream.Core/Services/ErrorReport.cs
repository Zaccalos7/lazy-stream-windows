using System.Text;
using System.Text.RegularExpressions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.SystemInfo;
using QRCoder;

namespace Orbis.Stream.Core.Services;

public static class ErrorReport
{
    public const string IssuesUrl = "https://github.com/Zaccalos7/lazy-stream-windows/issues/new";

    public static string Text(VideoEntity video, string? streamKey)
    {
        var error = video.Message ?? "";
        if (!string.IsNullOrEmpty(streamKey))
        {
            error = error.Replace(streamKey, "****", StringComparison.Ordinal);
            error = error.Replace(Uri.UnescapeDataString(streamKey), "****", StringComparison.Ordinal);
            var key = streamKey.Split('?')[0];
            if (!string.IsNullOrEmpty(key))
            {
                error = error.Replace(key, "****", StringComparison.Ordinal);
                error = error.Replace(Uri.UnescapeDataString(key), "****", StringComparison.Ordinal);
            }
            foreach (var parameter in streamKey.Split('?').Skip(1).SelectMany(query => query.Split('&')))
            {
                var equals = parameter.IndexOf('=');
                if (equals < 0 || parameter.Length - equals - 1 < 8) continue;
                var token = parameter[(equals + 1)..];
                error = error.Replace(token, "****", StringComparison.Ordinal);
                error = error.Replace(Uri.UnescapeDataString(token), "****", StringComparison.Ordinal);
            }
        }
        error = Regex.Replace(error, @"rtmps?://[^\s'""<>]+", "[stream URL hidden]", RegexOptions.IgnoreCase);
        return $"{AppInfo.Current.NameAndVersion}\nVideo #{video.Pkid} · {video.LiveStatus}\n{video.StartDateLive:O}\n\n{error}";
    }

    // QR payloads are UTF-8 bytes, not UTF-16 characters. Split only between Unicode scalars.
    public static IReadOnlyList<string> Parts(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 600)
            {
                parts.Add(current.ToString());
                current.Clear();
                bytes = 0;
            }
            current.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    public static string Image(string part, int index, int count)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode($"Orbis report {index + 1}/{count}\n{part}", QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        using var qr = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(qr.GetGraphic(5));
    }
}
