using System.Text;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Reports FFmpeg diagnostics while a publish is running and retains a bounded tail.</summary>
internal static class FfmpegProcessLog
{
    private const int TailLimit = 16_384;
    private const int LineLimit = 4_096;

    public static async Task<string> ReadAsync(StreamReader reader, ILogger logger, string platform, string role, string outputUrl)
    {
        var tail = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                // Mask before truncating: a long line cut halfway through a key must not leak it.
                var safe = FfmpegStreamingSession.RedactStreamKey(line, outputUrl);
                tail.AppendLine(safe);
                if (tail.Length > TailLimit)
                {
                    tail.Remove(0, tail.Length - TailLimit);
                }
                if (string.IsNullOrWhiteSpace(safe))
                {
                    continue;
                }

                var level = safe.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                    || safe.Contains("[fatal]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error
                    : safe.Contains("[warning]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning
                    : LogLevel.Information;
                logger.Log(level, "{Platform} ffmpeg {Role}: {Diagnostic}", platform, role,
                    safe.Length > LineLimit ? safe[..LineLimit] + "…" : safe);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A process closed during a stop: retain the lines already read.
        }
        return tail.ToString().Trim();
    }
}
