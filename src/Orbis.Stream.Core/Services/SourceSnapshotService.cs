using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// A still of a source, so the canvas shows what a tile will carry instead of an icon. The frame
/// comes from the same ffmpeg the live uses, which is also why it is only a still: a moving
/// preview would be a second ffmpeg per source for as long as the page is open.
/// </summary>
public sealed class SourceSnapshotService
{
    /// <summary>A camera takes a second or two to open; a device that takes longer is not coming.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private const int SnapshotWidth = 480;

    /// <summary>An overlay is usually the whole frame: it is drawn over the full width of the stage.</summary>
    private const int OverlayWidth = 960;

    private readonly FfmpegToolLocator _locator;
    private readonly ILogger<SourceSnapshotService> _logger;

    public SourceSnapshotService(FfmpegToolLocator locator, ILogger<SourceSnapshotService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>The JPEG of one frame, or null when the source cannot give one.</summary>
    public Task<byte[]?> GrabAsync(SourceKind kind, string? target, CancellationToken cancellationToken)
    {
        if (!IsSnapshottable(kind, target))
        {
            return Task.FromResult<byte[]?>(null);
        }

        var input = kind == SourceKind.File ? StreamingService.NormalizeUserPath(target!) : target!;
        return RunAsync(FfmpegCommandBuilder.BuildSnapshot(kind, input, SnapshotWidth), target!, cancellationToken);
    }

    /// <summary>
    /// The first frame of an overlay of the library, as a PNG with its alpha: what the layout page
    /// draws for a file the browser cannot play, and the face of a scene button. Not reachable from
    /// the snapshot route, which takes a path from the query string: the folder the file is read
    /// from has checked it is one of its own.
    /// </summary>
    internal Task<byte[]?> GrabOverlayAsync(
        string path, OverlayMedia media, CancellationToken cancellationToken, int width = OverlayWidth) =>
        RunAsync(FfmpegCommandBuilder.BuildSnapshot(SourceKind.Overlay, path, width, media), path, cancellationToken);

    private async Task<byte[]?> RunAsync(IReadOnlyList<string> arguments, string target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var process = new Process { StartInfo = _locator.CreateStartInfo(_locator.FfmpegPath, arguments) };

        try
        {
            process.Start();

            // stderr is drained as well: a device that complains a lot would otherwise fill the
            // pipe and hold ffmpeg before it writes the frame.
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var frame = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(frame, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0 || frame.Length == 0)
            {
                _logger.LogDebug("No snapshot of {Target}: {Errors}", target, (await errors.ConfigureAwait(false)).Trim());
                return null;
            }

            return frame.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("No snapshot of {Target} within {Timeout}", target, Timeout);
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(exception, "No snapshot of {Target}", target);
            return null;
        }
        finally
        {
            TryKill(process);
        }
    }

    /// <summary>
    /// Only what the catalog offers: this runs ffmpeg on a value that comes from a query string,
    /// so a target that is not a desktop, a monitor, a camera or a video file on disk is refused.
    /// An overlay is refused too: any picture on the disk would otherwise be one query away.
    /// </summary>
    internal static bool IsSnapshottable(SourceKind kind, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return kind switch
        {
            SourceKind.Screen => target == MonitorTarget.Desktop || MonitorTarget.TryParse(target, out _, out _, out _, out _),
            SourceKind.Camera => target.StartsWith("video=", StringComparison.Ordinal),
            SourceKind.File => VideoExtensions.IsVideoExtensionPresent(Path.GetExtension(target).TrimStart('.'))
                && File.Exists(StreamingService.NormalizeUserPath(target)),
            _ => false
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started, or already gone.
        }
    }
}
