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
    private int _stopRequested;

    private FfmpegStreamingSession(Process process, int videoPkid, string inputPath, ILogger logger)
    {
        _process = process;
        _logger = logger;
        VideoPkid = videoPkid;
        InputPath = inputPath;
        _standardError = process.StandardError.ReadToEndAsync();
    }

    public int VideoPkid { get; }

    public string InputPath { get; }

    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

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
        ILogger logger)
    {
        var arguments = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(inputPath, outputUrl, probe, setting));
        var startInfo = locator.CreateStartInfo(locator.FfmpegPath, arguments);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start ffmpeg for {inputPath}");
        }

        logger.LogInformation("ffmpeg started for {Input} -> {OutputUrl}", inputPath, outputUrl);
        return new FfmpegStreamingSession(process, videoPkid, inputPath, logger);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async Task<string> ReadErrorAsync() => await _standardError.ConfigureAwait(false);

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
            _process.Dispose();
        }
    }
}

/// <summary>Keeps track of the ffmpeg session of every streaming video so a stop request is instant.</summary>
public sealed class StreamingSessionRegistry
{
    private readonly ConcurrentDictionary<int, FfmpegStreamingSession> _sessions = new();

    public int ActiveCount => _sessions.Count;

    public void Register(FfmpegStreamingSession session) => _sessions[session.VideoPkid] = session;

    public void Remove(int videoPkid) => _sessions.TryRemove(videoPkid, out _);

    public bool TryGet(int videoPkid, out FfmpegStreamingSession? session) =>
        _sessions.TryGetValue(videoPkid, out session);

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
