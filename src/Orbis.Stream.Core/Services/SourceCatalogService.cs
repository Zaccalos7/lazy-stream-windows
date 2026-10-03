using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>One thing that can be dragged onto the canvas.</summary>
/// <param name="Id">Stable token the canvas stores. For a device it is the capture argument, not
/// the display name, so a device keeps working after the user renames it in Windows.</param>
/// <param name="Name">What the tile shows.</param>
/// <param name="Kind">How ffmpeg is asked to open it.</param>
/// <param name="Target">The capture argument: a gdigrab target, a dshow name, or a file path.</param>
/// <param name="Width">Natural size, when it is known. Null for a device that was not probed.</param>
/// <param name="Height">Natural size, when it is known.</param>
public sealed record SourceOption(
    string Id,
    string Name,
    SourceKind Kind,
    string Target,
    int? Width = null,
    int? Height = null);

/// <summary>
/// What the source picker can offer. Enumerating displays needs Win32 and enumerating cameras needs
/// the bundled ffmpeg, so both are behind this interface: on a machine without them the canvas
/// still works, it just offers the files.
/// </summary>
public interface ISourceProvider
{
    IReadOnlyList<SourceOption> List();
}

/// <summary>
/// The monitors of the machine, through <c>gdigrab</c>. gdigrab has no "list" mode and only knows
/// <c>desktop</c>, the whole virtual screen: one monitor is that same desktop cropped with an offset
/// and a size, so the target of a monitor carries its rectangle (see <see cref="MonitorTarget"/>)
/// and the command builder turns it back into those three options.
/// </summary>
public sealed class DisplaySourceProvider : ISourceProvider
{
    public IReadOnlyList<SourceOption> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return OnWindows();
        }
        catch (Exception)
        {
            // A display list is a convenience: a session without a desktop (a service, a locked
            // machine) still has to be able to stream the files it already has.
            return [];
        }
    }

    private static IReadOnlyList<SourceOption> OnWindows()
    {
        var monitors = DisplayMonitors.Enumerate();
        if (monitors.Count == 0)
        {
            return [];
        }

        // The virtual screen is the box around every monitor, not the sum of their widths: a
        // monitor stacked above another adds height, and one on the left has a negative origin.
        var left = monitors.Min(monitor => monitor.Left);
        var top = monitors.Min(monitor => monitor.Top);
        var right = monitors.Max(monitor => monitor.Left + monitor.Width);
        var bottom = monitors.Max(monitor => monitor.Top + monitor.Height);

        var options = new List<SourceOption>
        {
            new(MonitorTarget.Desktop, "Desktop", SourceKind.Screen, MonitorTarget.Desktop, right - left, bottom - top)
        };

        // With a single monitor the desktop already is that monitor: offering both is one choice twice.
        if (monitors.Count == 1)
        {
            return options;
        }

        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            var target = MonitorTarget.Of(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            options.Add(new(
                target,
                monitor.IsPrimary ? $"Screen {index + 1} ★" : $"Screen {index + 1}",
                SourceKind.Screen,
                target,
                monitor.Width,
                monitor.Height));
        }

        return options;
    }

    private static class DisplayMonitors
    {
        private const uint PrimaryMonitor = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int WorkLeft;
            public int WorkTop;
            public int WorkRight;
            public int WorkBottom;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        public static IReadOnlyList<Monitor> Enumerate()
        {
            var found = new List<Monitor>();

            EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (monitor, context, data) =>
                {
                    var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        found.Add(new Monitor(
                            info.Left,
                            info.Top,
                            info.Right - info.Left,
                            info.Bottom - info.Top,
                            (info.Flags & PrimaryMonitor) != 0));
                    }

                    return true;
                },
                IntPtr.Zero);

            // Left to right, then top to bottom: the order the user sees them in, so "Screen 1" is
            // the one on the left and not whichever the driver enumerated first.
            return [.. found.OrderBy(monitor => monitor.Left).ThenBy(monitor => monitor.Top)];
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr deviceContext,
            IntPtr clip,
            MonitorEnumProc callback,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr context, IntPtr data);
    }

    internal sealed record Monitor(int Left, int Top, int Width, int Height, bool IsPrimary);
}

/// <summary>
/// The cameras and the microphones, through the dshow device list the bundled ffmpeg prints. There
/// is no API for it, and the names it prints are exactly the names <c>-i video="…"</c> has to be
/// given, which is why the answer is used verbatim rather than translated through a lookup.
/// </summary>
public sealed class CameraSourceProvider : ISourceProvider
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The prefix every line of a dshow list has: <c>[dshow @ 000001d2]</c>.</summary>
    private static readonly Regex LinePrefix = new(@"^\[[^\]]*\]\s*", RegexOptions.Compiled);

    /// <summary>A device line: its name in quotes, and on ffmpeg 5+ the kind in brackets after it.</summary>
/// <summary>A device line: its name in quotes, and on ffmpeg 5+ the kind in brackets after it.</summary>
    private static readonly Regex DeviceLine = new(@"^""(?<name>[^""]+)""\s*(\((?<kind>[^)]*)\))?$", RegexOptions.Compiled);

    /// <summary>The alternative name (moniker) of a device, on the line under the friendly name.</summary>
    private static readonly Regex AlternativeNameLine = new(@"^Alternative name\s+""(?<altname>[^""]+)""$", RegexOptions.Compiled);

    private readonly FfmpegToolLocator _locator;

    public CameraSourceProvider(FfmpegToolLocator locator)
    {
        _locator = locator;
    }

    public IReadOnlyList<SourceOption> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var stderr = RunDeviceList();
        return stderr is null ? [] : Parse(stderr);
    }

    /// <summary>
    /// ffmpeg exits with an error on purpose here: <c>-i dummy</c> is not a device, and that is
    /// how the device list is asked for. The list is on stderr, so the exit code is not the test.
    /// </summary>
    private string? RunDeviceList()
    {
        string[] arguments = ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"];

        try
        {
            using var process = new Process { StartInfo = _locator.CreateStartInfo(_locator.FfmpegPath, arguments) };
            if (!process.Start())
            {
                return null;
            }

            // Read to the end first: the list is short and ffmpeg exits on its own once it has
            // printed everything.
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            return stderrTask.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
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
        catch (Exception)
        {
            // Nothing useful to do: the caller is already answering with no cameras.
        }
    }

    /// <summary>
    /// Reads the device list in both the shapes ffmpeg has printed it in. Up to 4.x it is a
    /// "DirectShow video devices" heading followed by the names; from 5.0 on each name says its own
    /// kind, <c>"Integrated Camera" (video)</c>. Every line carries the <c>[dshow @ …]</c> prefix,
    /// and the "Alternative name" line under each device is its moniker, which is not what a user
    /// recognises and is skipped.
    /// </summary>
    internal static IReadOnlyList<SourceOption> Parse(string deviceList)
    {
        var options = new List<SourceOption>();
        string? section = null;
        string? pendingName = null;
        string? pendingKind = null;

        foreach (var raw in deviceList.Split('\n'))
        {
            var line = LinePrefix.Replace(raw.Trim(), string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                section = "video";
                continue;
            }

            if (line.StartsWith("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                section = "audio";
                continue;
            }

            var match = DeviceLine.Match(line);
            if (match.Success)
            {
                if (pendingName is not null)
                {
                    AddPending(options, pendingName, pendingName, pendingKind);
                }

                pendingName = match.Groups["name"].Value;
                pendingKind = match.Groups["kind"].Success ? match.Groups["kind"].Value : section ?? string.Empty;
                continue;
            }

            var altMatch = AlternativeNameLine.Match(line);
            if (altMatch.Success && pendingName is not null)
            {
                var altName = altMatch.Groups["altname"].Value;

                // ffmpeg's dshow parser uses strtok on ':' which breaks on names containing a colon.
                // For those, we MUST use the alternative name (the device moniker) which has no colons.
                var targetName = pendingName.Contains(':', StringComparison.Ordinal) ? altName : pendingName;

                AddPending(options, targetName, pendingName, pendingKind);
                pendingName = null;
                pendingKind = null;
            }
        }

        if (pendingName is not null)
        {
            AddPending(options, pendingName, pendingName, pendingKind);
        }

        return options;
    }

    private static void AddPending(List<SourceOption> options, string targetName, string displayName, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        if (kind.Contains("video", StringComparison.OrdinalIgnoreCase))
        {
            Add(options, new SourceOption($"video={targetName}", displayName, SourceKind.Camera, $"video={targetName}"));
        }

        if (kind.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            Add(options, new SourceOption($"audio={targetName}", displayName, SourceKind.Microphone, $"audio={targetName}"));
        }
    }

    /// <summary>A device appears once per pinned instance; one entry is enough.</summary>
    private static void Add(List<SourceOption> options, SourceOption option)
    {
        if (options.All(existing => existing.Id != option.Id))
        {
            options.Add(option);
        }
    }
}

/// <summary>
/// The video files already on the machine. Same walk the folder flow does when a live starts, so
/// what the canvas offers is what a live from that folder would have played.
/// </summary>
public sealed class FileSourceProvider : ISourceProvider
{
    /// <summary>How many files a single folder contributes, so a library does not stall the page.</summary>
    public const int MaxFilesPerFolder = 200;

    private readonly IEnumerable<string> _folders;

    public FileSourceProvider(IEnumerable<string> folders)
    {
        _folders = folders;
    }

    public IReadOnlyList<SourceOption> List()
    {
        var options = new List<SourceOption>();

        foreach (var folder in _folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = StreamingService.NormalizeUserPath(folder);
            if (!System.IO.Directory.Exists(directory))
            {
                continue;
            }

            var count = 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(directory))
            {
                if (count >= MaxFilesPerFolder)
                {
                    break;
                }

                var extension = Path.GetExtension(path).TrimStart('.');
                if (!VideoExtensions.IsVideoExtensionPresent(extension))
                {
                    continue;
                }

                var full = Path.GetFullPath(path);
                options.Add(new SourceOption(full, Path.GetFileName(full), SourceKind.File, full));
                count++;
            }
        }

        return options;
    }
}

/// <summary>
/// Everything the source picker shows, in one call. The three providers cannot all fail the same
/// way: a missing ffmpeg costs the cameras, a headless session costs the displays, and neither
/// costs the files, so a provider that cannot answer returns nothing instead of failing the page.
/// </summary>
public sealed class SourceCatalogService
{
    private readonly IReadOnlyList<ISourceProvider> _providers;
    private readonly ILogger<SourceCatalogService> _logger;

    public SourceCatalogService(IEnumerable<ISourceProvider> providers, ILogger<SourceCatalogService> logger)
    {
        _providers = [.. providers];
        _logger = logger;
    }

    public IReadOnlyList<SourceOption> List()
    {
        var options = new List<SourceOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in _providers)
        {
            try
            {
                foreach (var option in provider.List())
                {
                    if (seen.Add(option.Id))
                    {
                        options.Add(option);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception, "{Provider} could not list its sources", provider.GetType().Name);
            }
        }

        return options;
    }
}
