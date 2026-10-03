using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>A running ffmpeg process, killable on demand (the .NET equivalent of stopping the recorder).</summary>
public sealed class FfmpegStreamingSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Task<string> _standardError;
    private readonly Task _standardOutput;
    private readonly long _resumedFromMilliseconds;
    private readonly string _outputUrl;
    private long _positionMilliseconds;
    private int _stopRequested;
    private int _restartRequested;
    private int _endedNaturally;

    private FfmpegStreamingSession(
        Process process,
        int videoPkid,
        string inputPath,
        MediaProbeResult probe,
        MediaOutput output,
        TimeSpan resumeFrom,
        string outputUrl,
        ILogger logger)
    {
        _process = process;
        _logger = logger;
        _outputUrl = outputUrl;

        // ffmpeg counts from where it was asked to seek to, not from the start of the file: the
        // position of the live is that count plus the point it resumed from. Without it a stop after
        // a resume recorded how long the last pass lasted, and the next play went back in time.
        _resumedFromMilliseconds = (long)Math.Max(0, resumeFrom.TotalMilliseconds);
        _positionMilliseconds = _resumedFromMilliseconds;
        VideoPkid = videoPkid;
        InputPath = inputPath;
        Probe = probe;
        Output = output;
        _standardError = ReadToEndAsync(process.StandardError);

        // Nothing else reads what ffmpeg writes on its standard output, and the pipe it writes
        // <c>-progress</c> into is small: a session that left it alone would block ffmpeg itself.
        _standardOutput = FollowProgressAsync();
    }

    public int VideoPkid { get; }

    public string InputPath { get; }

    /// <summary>
    /// What ffprobe read from the file, kept so the preview knows the source without asking again.
    /// For a canvas it is what the composition was built as (see <see cref="StartComposition"/>).
    /// </summary>
    public MediaProbeResult Probe { get; }

    /// <summary>
    /// What this process was started to produce, kept because the configuration can change under
    /// a running transcode: until the new ffmpeg is up, these are the numbers being sent.
    /// </summary>
    public MediaOutput Output { get; }

    /// <summary>When this ffmpeg was started: it is what tells one running live from another.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>How far the transcode got, as ffmpeg last reported it. Zero until the first report.</summary>
    public long PositionMilliseconds => Interlocked.Read(ref _positionMilliseconds);

    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    /// <summary>
    /// Set when the encoder configuration changed under a running transcode. ffmpeg cannot change
    /// its options halfway, so the answer is to start it again from where it got to: the streaming
    /// loop watches this flag and does exactly that.
    /// </summary>
    public bool RestartRequested => Volatile.Read(ref _restartRequested) == 1;

    public bool EndedNaturally => Volatile.Read(ref _endedNaturally) == 1;

    public async Task EndNaturallyAsync()
    {
        if (Interlocked.Exchange(ref _endedNaturally, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static FfmpegStreamingSession Start(
        FfmpegToolLocator locator,
        int videoPkid,
        string inputPath,
        string outputUrl,
        VideoSettingEntity setting,
        MediaProbeResult probe,
        ILogger logger,
        TimeSpan resumeFrom = default,
        string? previewPath = null)
    {
        var arguments = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(inputPath, outputUrl, probe, setting, resumeFrom, previewPath));
        return Launch(locator, videoPkid, inputPath, outputUrl, arguments, probe, FfmpegCommandBuilder.ResolveOutput(setting, probe), resumeFrom, logger);
    }

    /// <summary>
    /// The same process, started on a canvas instead of a file. The canvas has no container to
    /// probe, so the probe it keeps describes what the composition was built as: its size, its
    /// rate, and whether a sound is mixed into it. The preview reads a session the same way
    /// whatever it streams, and a missing probe was a page that could not be drawn.
    /// </summary>
    public static FfmpegStreamingSession StartComposition(
        FfmpegToolLocator locator,
        int videoPkid,
        IReadOnlyList<FfmpegCompositionItem> items,
        string outputUrl,
        VideoSettingEntity setting,
        int canvasWidth,
        int canvasHeight,
        double canvasFrameRate,
        ILogger logger,
        TimeSpan resumeFrom = default,
        string? previewPath = null)
    {
        var arguments = FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            items, outputUrl, setting, canvasWidth, canvasHeight, canvasFrameRate, resumeFrom, previewPath));

        // The composition is always sent at the size of the canvas: the resolution of the setting
        // is not applied on top of it (see BuildComposition), so it is not the one shown either.
        var frameRate = setting.FrameRate is > 0 ? setting.FrameRate.Value : canvasFrameRate;
        var output = new MediaOutput(canvasWidth, canvasHeight, frameRate);
        var sound = FfmpegCommandBuilder.CarriesSound(items);
        var probe = new MediaProbeResult(canvasWidth, canvasHeight, frameRate, sound, sound ? 2 : 0, 0);

        return Launch(locator, videoPkid, SceneDescriptionOf(items), outputUrl, arguments, probe, output, resumeFrom, logger);
    }

    private static FfmpegStreamingSession Launch(
        FfmpegToolLocator locator,
        int videoPkid,
        string inputPath,
        string outputUrl,
        IReadOnlyList<string> arguments,
        MediaProbeResult probe,
        MediaOutput output,
        TimeSpan resumeFrom,
        ILogger logger)
    {
        var startInfo = locator.CreateStartInfo(locator.FfmpegPath, arguments);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start ffmpeg for {inputPath}");
        }

        // The output is named, not read off the end of the command line: the preview comes after it.
        logger.LogInformation(
            "ffmpeg started for {Input} -> {OutputUrl}", inputPath, RedactStreamKey(outputUrl, outputUrl));
        return new FfmpegStreamingSession(process, videoPkid, inputPath, probe, output, resumeFrom, outputUrl, logger);
    }

    /// <summary>What the pages show instead of a path when a live is streaming a canvas.</summary>
    private static string SceneDescriptionOf(IReadOnlyList<FfmpegCompositionItem> items) =>
        items.Count == 1 ? items[0].Target : $"{items.Count} sources";

    /// <summary>Asks the streaming loop to start this transcode again with a new configuration.</summary>
    public void RequestRestart() => Interlocked.Exchange(ref _restartRequested, 1);

    /// <summary>
    /// Reads the <c>-progress</c> block of ffmpeg, which repeats the state of the transcode until
    /// the process ends. <c>out_time_us</c> is how far the output got since the point the input was
    /// seeked to, in microseconds; the sibling <c>out_time_ms</c> counts microseconds too, which is
    /// why it is not the one used here.
    /// </summary>
    private async Task FollowProgressAsync()
    {
        const string key = "out_time_us=";

        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith(key, StringComparison.Ordinal)
                    && long.TryParse(
                        line[key.Length..].AsSpan(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var microseconds))
                {
                    Interlocked.Exchange(ref _positionMilliseconds, _resumedFromMilliseconds + microseconds / 1000);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // The process was killed or disposed: the last position reported is the one that counts.
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>
    /// What ffmpeg wrote on its standard error, with the stream key masked: ffmpeg names the output
    /// url in every error about it, and this text ends up on the live history page and in the log,
    /// where a key in clear is a key anyone looking at the screen can stream with.
    /// </summary>
    public async Task<string> ReadErrorAsync() =>
        RedactStreamKey(await _standardError.ConfigureAwait(false), _outputUrl);

    /// <summary>
    /// Replaces the stream key - the last segment of the output url - wherever it appears in a text.
    /// A url with no segment worth hiding (a file on disk, a key too short to be one) is left alone.
    /// </summary>
    public static string RedactStreamKey(string text, string outputUrl)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(outputUrl);

        if (!outputUrl.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }

        var key = outputUrl.TrimEnd('/');
        key = key[(key.LastIndexOf('/') + 1)..];
        return key.Length < 8 ? text : text.Replace(key, "****", StringComparison.Ordinal);
    }

    /// <summary>Drains a pipe of the process: a disposed process throws instead of ending the read.</summary>
    private static async Task<string> ReadToEndAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Stops the transcode, killing the whole ffmpeg process tree.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("ffmpeg stopped for {Input}", InputPath);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Nothing to clean up.
        }
        finally
        {
            // Both readers end on their own once the pipe is gone, and neither of them can fault:
            // waiting for them releases the last reference to the process before it is disposed.
            await Task.WhenAll(_standardError, _standardOutput).ConfigureAwait(false);
            _process.Dispose();
        }
    }
}

/// <summary>Keeps track of the ffmpeg session of every streaming video so a stop request is instant.</summary>
public sealed class StreamingSessionRegistry
{
    private readonly ConcurrentDictionary<int, FfmpegStreamingSession> _sessions = new();

    public int ActiveCount => _sessions.Count;

    /// <summary>
    /// The running transcodes, most recently started first. The dictionary hands out a snapshot, so
    /// the preview can walk them while a start or a stop is changing the map.
    /// </summary>
    public IReadOnlyList<FfmpegStreamingSession> Running =>
        _sessions.Values.OrderByDescending(session => session.StartedAt).ToList();

    public void Register(FfmpegStreamingSession session) => _sessions[session.VideoPkid] = session;

    public void Remove(int videoPkid) => _sessions.TryRemove(videoPkid, out _);

    public bool TryGet(int videoPkid, out FfmpegStreamingSession? session) =>
        _sessions.TryGetValue(videoPkid, out session);

    /// <summary>
    /// The session a preview is about: the one asked for when it is still running, otherwise the
    /// transcode that started last. Null when nothing is being sent.
    /// </summary>
    public FfmpegStreamingSession? Watched(int? videoPkid) =>
        videoPkid is { } wanted && _sessions.TryGetValue(wanted, out var asked) && !asked.HasExited
            ? asked
            : _sessions.Values.Where(session => !session.HasExited)
                .OrderByDescending(session => session.StartedAt)
                .FirstOrDefault();

    /// <summary>Asks a running transcode to start again with a new configuration. False if it is gone.</summary>
    public bool RequestRestart(int videoPkid)
    {
        if (!_sessions.TryGetValue(videoPkid, out var session) || session is null)
        {
            return false;
        }

        session.RequestRestart();
        return true;
    }

    public Task StopAsync(int videoPkid)
    {
        return _sessions.TryGetValue(videoPkid, out var session) && session is not null
            ? session.StopAsync()
            : Task.CompletedTask;
    }

    public async Task StopAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.StopAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
