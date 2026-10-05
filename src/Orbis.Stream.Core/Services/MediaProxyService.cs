using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>The file, or its copy, a live opens for a tile, and whether the GPU decodes it.</summary>
public sealed record MediaInput(string Path, bool HardwareDecoding);

/// <summary>
/// Light copies of the files of a canvas that are bigger than Full HD. A 4K, 10 bit, HDR file costs
/// more to decode than a whole live has: two of them on a scene are decoded at 0.6x real time on
/// eight cores, and the live goes in slow motion however it is paced. The same picture as 1080p,
/// 8 bit H.264 decodes more than ten times faster, so the canvas streams that instead. A file of
/// Full HD or less is streamed as it is, whatever it is encoded with.
/// <para>A copy costs as much as decoding the file once, which is exactly what a live cannot
/// afford: made during the live it would take the CPU the live needs. So it is made as soon as
/// the file is laid on a canvas, long before a live usually starts, one file at a time and below
/// the priority of everything else, and it is kept for every live after. A live that starts
/// before it is ready streams the file as it is and asks for the copy, which goes on with what
/// CPU the live leaves over and is there for the next one.</para>
/// <para>The GPU makes the copy when there is one and it is faster: a few seconds of the file are
/// made each way before the copy starts, and the fastest way makes it. It is measured rather than
/// assumed because a decoded 4K frame has to come back from the GPU to be scaled, and on some
/// machines that copy costs more than decoding on the CPU (four times slower on an Intel iGPU
/// under VA-API). A live of the file before its copy is ready decodes it the way that won.</para>
/// <para>A copy belongs to one version of a file: its name is a hash of the path, the size and the
/// time it was last written, so a file that changes is a file with no copy yet. Copies nobody
/// streamed for <see cref="UnusedFor"/> are deleted.</para>
/// </summary>
public sealed class MediaProxyService : IDisposable
{
    /// <summary>Full HD: the long and the short side of the box a copy fits in, on its side for a portrait file.</summary>
    public const int LongSide = 1920;

    public const int ShortSide = 1080;

    private const string DirectoryName = "proxies";
    private const string Extension = ".mp4";
    private const string PartialExtension = ".part";

    /// <summary>Part of the name of a copy: a change to how copies are made makes new ones.</summary>
    private const int Recipe = 1;

    private static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    /// <summary>How much of the file each way of making the copy is timed on.</summary>
    private static readonly TimeSpan Trial = TimeSpan.FromSeconds(3);

    /// <summary>The GPU encoders tried, in order; the first one that opens on this machine is used.</summary>
    private static readonly string[] HardwareEncoders = ["h264_nvenc", "h264_qsv", "h264_amf"];

    /// <summary>How often a long copy says in the log how far it got, in tenths of the file.</summary>
    private const int ProgressSteps = 10;

    private readonly string _directory;
    private readonly FfmpegToolLocator _locator;
    private readonly FfmpegProbe _probe;
    private readonly ILogger<MediaProxyService> _logger;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>Per version of a file: whether the GPU decoded it faster than the CPU did.</summary>
    private readonly Dictionary<string, bool> _hardwareDecoding = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lazy<Task<bool>> _canToneMap;
    private readonly Lazy<Task<string?>> _hardwareEncoder;
    private Task? _worker;

    public MediaProxyService(
        OrbisRuntimeOptions options, FfmpegToolLocator locator, FfmpegProbe probe, ILogger<MediaProxyService> logger)
        : this(Path.Combine(options.DataDirectory, DirectoryName), locator, probe, logger)
    {
    }

    public MediaProxyService(string directory, FfmpegToolLocator locator, FfmpegProbe probe, ILogger<MediaProxyService> logger)
    {
        _directory = directory;
        _locator = locator;
        _probe = probe;
        _logger = logger;
        _canToneMap = new(CanToneMapAsync);
        _hardwareEncoder = new(FindHardwareEncoderAsync);
    }

    /// <summary>
    /// Whether a file is streamed from a copy: bigger than Full HD, either way up. A file of Full HD
    /// or less keeps its own picture, even in 10 bit or HDR: a copy would be a second generation of
    /// it, and the decode it saves is the smaller part of what the size costs.
    /// </summary>
    public static bool Needs(MediaProbeResult probe) =>
        Math.Max(probe.Width, probe.Height) > LongSide || Math.Min(probe.Width, probe.Height) > ShortSide;

    /// <summary>PQ (HDR10) or HLG: the transfers an SDR live has to be tone mapped from.</summary>
    public static bool IsHdr(MediaProbeResult probe) =>
        probe.ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>The box of a file this shape: Full HD, on its side for a portrait file.</summary>
    public static (int Width, int Height) BoxOf(int width, int height) =>
        height > width ? (ShortSide, LongSide) : (LongSide, ShortSide);

    /// <summary>The size of the copy of a file this size: the box, the shape of the file, never larger.</summary>
    public static (int Width, int Height) SizeOf(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        var (boxWidth, boxHeight) = BoxOf(width, height);
        var scale = Math.Min(1d, Math.Min((double)boxWidth / width, (double)boxHeight / height));
        return (Even((int)(width * scale)), Even((int)(height * scale)));
    }

    /// <summary>
    /// Asks for the copy of a file, if it needs one and has none: the file has just been laid on a
    /// canvas. Returns at once; the copy is made in the background, one file at a time.
    /// </summary>
    public void Prepare(string path)
    {
        if (KeyOf(path) is not { } key)
        {
            return;
        }

        lock (_queued)
        {
            if (File.Exists(PathOf(key)) || _failed.Contains(key) || !_queued.Add(key))
            {
                return;
            }

            _worker ??= Task.Run(WorkAsync);
        }

        _queue.Writer.TryWrite(path);
    }

    /// <summary>
    /// What a live should open for a file on a tile this size: its copy when there is one and the
    /// tile is no bigger than the copy, otherwise the file, decoded by the GPU if the GPU was
    /// measured faster on it. A heavy file with no copy yet gets one asked for, for the next live.
    /// </summary>
    public MediaInput Resolve(string path, MediaProbeResult probe, int tileWidth, int tileHeight)
    {
        var original = new MediaInput(path, false);
        if (!Needs(probe))
        {
            return original;
        }

        // Scaling the copy up to a tile bigger than it is a worse picture than scaling the file
        // down: that tile pays for the file as it is.
        var (width, height) = SizeOf(probe.Width, probe.Height);
        if (tileWidth > width || tileHeight > height)
        {
            _logger.LogInformation(
                "{File} is on a {TileWidth}x{TileHeight} tile, bigger than its {Width}x{Height} copy: streaming the file as it is",
                Path.GetFileName(path), tileWidth, tileHeight, width, height);
            return original;
        }

        if (KeyOf(path) is not { } key)
        {
            return original;
        }

        var proxy = PathOf(key);
        if (File.Exists(proxy))
        {
            // Used: the clock of the clean-up starts again.
            TryTouch(proxy);
            _logger.LogInformation("{File} streams from its {Width}x{Height} copy", Path.GetFileName(path), width, height);
            return new MediaInput(proxy, false);
        }

        bool gpu;
        lock (_queued)
        {
            gpu = _hardwareDecoding.GetValueOrDefault(key);
        }

        _logger.LogWarning(
            "{File} ({Width}x{Height} {PixelFormat}) has no light copy yet: streaming it as it is with {Decoder} decoding, which may be slower than real time; the copy is being made for the next live",
            Path.GetFileName(path), probe.Width, probe.Height, probe.PixelFormat ?? "?", gpu ? "GPU" : "CPU");
        Prepare(path);
        return original with { HardwareDecoding = gpu };
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _stopping.Cancel();
            _worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WorkAsync()
    {
        var token = _stopping.Token;
        DeleteStale();

        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                // The key it was queued under: a file written to while it waited is a new version,
                // and the next drop of it asks again.
                var key = KeyOf(path);
                try
                {
                    if (key is not null)
                    {
                        await MakeAsync(path, key, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "No light copy of {File}", Path.GetFileName(path));
                }
                finally
                {
                    lock (_queued)
                    {
                        _queued.Remove(key ?? string.Empty);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task MakeAsync(string path, string key, CancellationToken token)
    {
        if (File.Exists(PathOf(key)))
        {
            return;
        }

        var probe = await _probe.ProbeAsync(path, token).ConfigureAwait(false);
        if (!Needs(probe))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var proxy = PathOf(key);
        var partial = proxy + PartialExtension;
        var toneMap = IsHdr(probe) && await _canToneMap.Value.ConfigureAwait(false);
        var (width, height) = SizeOf(probe.Width, probe.Height);
        var (boxWidth, boxHeight) = BoxOf(probe.Width, probe.Height);
        var encoding = await FastestEncodingAsync(path, key, boxWidth, boxHeight, toneMap, token).ConfigureAwait(false);

        _logger.LogInformation(
            "Making a light copy of {File}: {SourceWidth}x{SourceHeight} {PixelFormat}{Hdr} to {Width}x{Height} H.264, {Encoding}",
            Path.GetFileName(path), probe.Width, probe.Height, probe.PixelFormat ?? "?",
            IsHdr(probe) ? (toneMap ? " HDR, tone mapped" : " HDR, not tone mapped (no zscale in this ffmpeg)") : string.Empty,
            width, height, encoding);

        var started = Stopwatch.GetTimestamp();
        using var process = new Process
        {
            StartInfo = _locator.CreateStartInfo(
                _locator.FfmpegPath, FfmpegCommandBuilder.BuildProxy(path, partial, boxWidth, boxHeight, toneMap, encoding))
        };

        process.Start();
        Lower(process);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await FollowProgressAsync(process, path, probe.DurationSeconds, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            TryDelete(partial);
            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        if (process.ExitCode != 0)
        {
            TryDelete(partial);
            lock (_queued)
            {
                _failed.Add(key);
            }

            _logger.LogWarning(
                "No light copy of {File}: ffmpeg exited with {ExitCode}: {Errors}",
                Path.GetFileName(path), process.ExitCode, (await errors.ConfigureAwait(false)).Trim());
            return;
        }

        File.Move(partial, proxy, overwrite: true);
        _logger.LogInformation(
            "The light copy of {File} is ready: {Minutes:0.0} minutes, {Speed:0.00}x real time",
            Path.GetFileName(path),
            elapsed.TotalMinutes,
            probe.DurationSeconds > 0 ? probe.DurationSeconds / Math.Max(1, elapsed.TotalSeconds) : 0);
    }

    /// <summary>
    /// Times the first seconds of the copy each way this machine can make it, and answers the
    /// fastest. The CPU is always one of them; the GPU ones are the decoder, the encoder, or both,
    /// as far as there is a GPU to do them. A way that fails is not a way.
    /// </summary>
    private async Task<ProxyEncoding> FastestEncodingAsync(
        string path, string key, int boxWidth, int boxHeight, bool toneMap, CancellationToken token)
    {
        var encoder = await _hardwareEncoder.Value.ConfigureAwait(false);
        ProxyEncoding[] candidates = encoder is null
            ? [ProxyEncoding.Cpu, new(true, null)]
            : [ProxyEncoding.Cpu, new(false, encoder), new(true, encoder)];

        var best = ProxyEncoding.Cpu;
        var bestTime = TimeSpan.MaxValue;
        var timings = new List<string>();
        foreach (var candidate in candidates)
        {
            var arguments = FfmpegCommandBuilder.BuildProxy(path, "-", boxWidth, boxHeight, toneMap, candidate, Trial);
            var started = Stopwatch.GetTimestamp();
            bool worked;
            try
            {
                await _locator.RunCapturedAsync(_locator.FfmpegPath, arguments, token).ConfigureAwait(false);
                worked = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                worked = false;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            timings.Add(worked ? string.Create(CultureInfo.InvariantCulture, $"{candidate} {elapsed.TotalSeconds:0.0}s") : $"{candidate} failed");
            if (worked && elapsed < bestTime)
            {
                (best, bestTime) = (candidate, elapsed);
            }
        }

        lock (_queued)
        {
            _hardwareDecoding[key] = best.HardwareDecoding;
        }

        _logger.LogInformation(
            "Light copy of {File}, {Seconds}s timed each way: {Timings}; the copy is made with {Best}",
            Path.GetFileName(path), Trial.TotalSeconds, string.Join(", ", timings), best);
        return best;
    }

    /// <summary>The first GPU encoder that encodes a few frames on this machine, or null.</summary>
    private async Task<string?> FindHardwareEncoderAsync()
    {
        foreach (var encoder in HardwareEncoders)
        {
            try
            {
                await _locator.RunCapturedAsync(
                    _locator.FfmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x144:r=30",
                        "-frames:v", "5", "-c:v", encoder, "-f", "null", "-"],
                    CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("GPU encoder found: {Encoder}", encoder);
                return encoder;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
        }

        _logger.LogInformation("No GPU encoder opens on this machine: light copies are encoded by the CPU");
        return null;
    }

    /// <summary>A file of hours takes hours: the log says every tenth of the way how far it got.</summary>
    private async Task FollowProgressAsync(Process process, string path, double durationSeconds, CancellationToken token)
    {
        var reported = 0;
        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (durationSeconds <= 0
                || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
                || !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
            {
                continue;
            }

            var step = (int)(microseconds / 1_000_000d / durationSeconds * ProgressSteps);
            if (step > reported && step < ProgressSteps)
            {
                reported = step;
                _logger.LogInformation(
                    "Light copy of {File}: {Percent}%", Path.GetFileName(path), step * 100 / ProgressSteps);
            }
        }
    }

    /// <summary>The copy waits for whatever else wants the CPU, above all a live.</summary>
    private void Lower(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogDebug(exception, "The light copy runs at normal priority");
        }
    }

    /// <summary>Tone mapping needs zscale (zimg), which not every ffmpeg build has.</summary>
    private async Task<bool> CanToneMapAsync()
    {
        try
        {
            var filters = await _locator
                .RunCapturedAsync(_locator.FfmpegPath, ["-hide_banner", "-filters"], CancellationToken.None)
                .ConfigureAwait(false);
            return filters.Contains(" zscale ", StringComparison.Ordinal) && filters.Contains(" tonemap ", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Half made copies of a run that was closed, and copies nobody streamed for a month.</summary>
    private void DeleteStale()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var unusedSince = DateTime.UtcNow - UnusedFor;
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles())
        {
            if (file.Name.EndsWith(PartialExtension, StringComparison.Ordinal)
                || (file.Extension == Extension && file.LastWriteTimeUtc < unusedSince))
            {
                TryDelete(file.FullName);
            }
        }
    }

    /// <summary>
    /// The name of the copy of this version of the file, or null when there is no file. The path
    /// is the one the composer and the live both resolve to; Windows does not tell case apart.
    /// </summary>
    private static string? KeyOf(string path)
    {
        try
        {
            var file = new FileInfo(StreamingService.NormalizeUserPath(path));
            if (!file.Exists)
            {
                return null;
            }

            var name = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
            var identity = string.Create(CultureInfo.InvariantCulture,
                $"{Recipe}|{name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string PathOf(string key) => Path.Combine(_directory, key + Extension);

    private static int Even(int value) => value - (value % 2);

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
