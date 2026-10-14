using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming.Rtmp;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The connection of one live to its ingest, kept open across the encoders that feed it.
/// <para>A live is many ffmpeg processes in a row: the videos of a playlist, a spot in the middle
/// of one, the pass started again after a change of parameters. When every one of them opened its
/// own publish, every change was a live that ended and a new one that started, and the platforms
/// show that as the stream going off air for as long as the new publish takes to be watchable.
/// Here the publish is opened once and each encoder is laid after the one before it on the same
/// timeline (see <see cref="FlvPacedRelay.Attach"/>): the viewers see one stream, and the jitter
/// buffer of the relay covers the moment the next encoder takes to start.</para>
/// <para>A connection that breaks is not repaired in place: the next encoder attached opens a new
/// one, which is the reconnection the streaming loop already decides on.</para>
/// </summary>
public sealed class LiveOutput : IAsyncDisposable
{
    /// <summary>How long a sender ffmpeg may take to close its publish once its input is over.</summary>
    private static readonly TimeSpan SenderGrace = TimeSpan.FromSeconds(5);

    private readonly FfmpegToolLocator _locator;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private FlvPacedRelay? _relay;
    private Process? _sender;
    private Task<string>? _senderErrors;
    private long _publishedMilliseconds;
    private long _lastPublishAdvanceTicks = Environment.TickCount64;
    private int _senderGeneration;
    private MediaOutput? _frame;
    private EncoderQuality? _qualityCap;

    private LiveOutput(string outputUrl, StreamPlatformProfile profile, FfmpegToolLocator locator, ILogger logger)
    {
        OutputUrl = outputUrl;
        Profile = profile;
        Ladder = new BitrateLadder(profile.Adaptation, profile.MaxLead);
        _locator = locator;
        _logger = logger;
    }

    /// <summary>
    /// The shared connection of a live, for the platforms that go through the relay. A custom
    /// ingest has no relay: every ffmpeg talks to it on its own, as it always did, and null says so.
    /// </summary>
    /// <param name="transport">
    /// What carries the paced stream to the ingest when the configuration of the channel asks for
    /// something other than the transport of the platform; null keeps the one of the platform. A
    /// platform paced by its sender (YouTube) keeps the sender whatever is asked.
    /// </param>
    /// <param name="profile">A known ingest profile when publishing through another endpoint.</param>
    public static LiveOutput? For(string outputUrl, FfmpegToolLocator locator, ILogger logger, RelayTransport? transport = null,
        StreamPlatformProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(outputUrl);
        profile ??= StreamPlatformProfile.For(outputUrl);
        if (!profile.UsesRelay)
        {
            return null;
        }

        return new LiveOutput(outputUrl, profile.WithTransport(transport), locator, logger);
    }

    public string OutputUrl { get; }

    // A write to the sender's stdin only fills a local pipe. Its output progress is the
    // evidence that data was written to the ingest.
    public long PublishedMilliseconds => Interlocked.Read(ref _publishedMilliseconds);
    public long LastPublishAdvanceTicks => Interlocked.Read(ref _lastPublishAdvanceTicks);
    public bool SenderHasExited
    {
        get
        {
            lock (_gate)
            {
                return Profile.Transport == RelayTransport.FfmpegSender
                    && (_sender is null || _sender.HasExited);
            }
        }
    }

    public async Task<string> ReadSenderErrorAsync()
    {
        Task<string>? errors;
        lock (_gate)
        {
            errors = _sender is null or { HasExited: true } ? _senderErrors : null;
        }
        return errors is null ? string.Empty : await errors.ConfigureAwait(false);
    }

    /// <summary>
    /// The picture of the live, fixed by the first encoder that asks: every video, spot and pass
    /// after it is fitted into the same size and rate, because an ingest is not told the stream
    /// changed format halfway and YouTube stops processing one that does.
    /// </summary>
    public MediaOutput? Frame
    {
        get
        {
            lock (_gate)
            {
                return _frame;
            }
        }
    }

    /// <summary>The picture of the live: the one fixed already, or this one, which becomes it.</summary>
    public MediaOutput Pin(MediaOutput wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (_gate)
        {
            return _frame ??= wanted;
        }
    }

    public StreamPlatformProfile Profile { get; }

    /// <summary>
    /// The highest encoder level the rest of this live may use, once its encoder was found not to
    /// keep up (<see cref="TryLower"/>); null while it does. It lasts as long as the live: a pass
    /// that starts again at the level that could not keep up falls behind again.
    /// </summary>
    public EncoderQuality? QualityCap
    {
        get
        {
            lock (_gate)
            {
                return _qualityCap;
            }
        }
    }

    /// <summary>
    /// Lowers the level of the live one step below <paramref name="current"/>: false when it is
    /// the lightest already, and there is nothing left to lower.
    /// </summary>
    public bool TryLower(EncoderQuality current, out EncoderQuality lower)
    {
        lower = current switch
        {
            EncoderQuality.High => EncoderQuality.Balanced,
            EncoderQuality.Balanced => EncoderQuality.Light,
            _ => current
        };

        if (lower == current)
        {
            return false;
        }

        lock (_gate)
        {
            _qualityCap = _qualityCap is { } cap && cap < lower ? cap : lower;
            lower = _qualityCap.Value;
        }

        return true;
    }

    /// <summary>The level a pass of this live goes out at: the one asked for, under the cap.</summary>
    public EncoderQuality Cap(EncoderQuality wanted) =>
        QualityCap is { } cap && wanted != EncoderQuality.Auto && wanted > cap ? cap : wanted;

    /// <summary>
    /// The video bitrate of the live, following what the network carries. It lasts as long as the
    /// live, across its passes and its reconnections, as the quality cap does: the next file of a
    /// playlist goes out on the network the last one could not get through, not on a fresh guess.
    /// </summary>
    public BitrateLadder Ladder { get; }

    /// <summary>
    /// Reads the connection and says whether the video bitrate of the live has to change (see
    /// <see cref="BitrateLadder.Observe"/>): null while it holds, and while there is no open
    /// connection to read.
    /// </summary>
    public RateDecision? AdaptBitrate(int nominal, int audioBitrate)
    {
        FlvPacedRelay? relay;
        lock (_gate)
        {
            relay = _relay;
        }

        return relay is { IsOpen: true } ? Ladder.Observe(relay.Sample(), nominal, audioBitrate) : null;
    }

    /// <summary>
    /// Hands the FLV an encoder writes to the connection, opening it first when there is none or
    /// the last one is gone.
    /// </summary>
    public RelaySegment Attach(System.IO.Stream encoderOutput)
    {
        ArgumentNullException.ThrowIfNull(encoderOutput);

        lock (_gate)
        {
            if (_relay is { IsOpen: true } relay)
            {
                try
                {
                    return relay.Attach(encoderOutput);
                }
                catch (InvalidOperationException)
                {
                    // The connection broke between the check and the attach: a new one it is.
                }
            }

            CloseQuietly();
            _relay = Open();
            return _relay.Attach(encoderOutput);
        }
    }

    /// <summary>The live is over: what is queued goes on air, then the publish is closed cleanly.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        FlvPacedRelay? relay;
        Process? sender;
        lock (_gate)
        {
            relay = _relay;
            sender = _sender;
        }

        if (relay is null)
        {
            return;
        }

        relay.Close();
        try
        {
            await relay.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abort();
            throw;
        }
        catch (Exception exception)
        {
            // A connection that broke on the last seconds has nothing left to send: the encoders
            // already reported what went wrong.
            _logger.LogDebug(exception, "The connection to {Platform} ended badly", Profile.Platform);
        }

        if (sender is not null)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(SenderGrace);
            try
            {
                await sender.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(sender);
            }
        }
    }

    /// <summary>Closes the connection at once, whatever is queued: the live was stopped or broke.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            CloseQuietly();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            CloseQuietly();
            _relay = null;
        }

        return ValueTask.CompletedTask;
    }

    private FlvPacedRelay Open()
    {
        IFlvSink sink;
        if (Profile.Transport == RelayTransport.NativeRtmp)
        {
            sink = new RtmpPublisher(OutputUrl, Profile.ConnectTimeout, Profile.StallTimeout, _logger);
        }
        else
        {
            _sender = StartSender();
            sink = new StreamFlvSink(_sender.StandardInput.BaseStream);
        }

        var relay = new FlvPacedRelay(sink, Profile, _logger);
        relay.Start();
        _logger.LogInformation(
            "Publisher to {Platform} started for the whole live, using {Transport}, paced by {Pacing}; waiting for the ingest: {OutputUrl}",
            Profile.Platform,
            Profile.Transport == RelayTransport.NativeRtmp ? "the native RTMP publisher" : "ffmpeg",
            Profile.Pacing == RelayPacing.Sender ? "ffmpeg" : "the relay",
            FfmpegStreamingSession.RedactStreamKey(OutputUrl, OutputUrl));
        return relay;
    }

    private Process StartSender()
    {
        var startInfo = _locator.CreateStartInfo(_locator.FfmpegPath, FfmpegCommandBuilder.BuildSender(OutputUrl, Profile));
        startInfo.RedirectStandardInput = true;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the ffmpeg sender");
        }

        // Drain the sender's progress and use it to measure publication, keeping its errors
        // for the live history as well as the log.
        Interlocked.Exchange(ref _publishedMilliseconds, 0);
        Interlocked.Exchange(ref _lastPublishAdvanceTicks, Environment.TickCount64);
        _ = FollowSenderProgressAsync(process, Interlocked.Increment(ref _senderGeneration));
        _senderErrors = ReportSenderErrorsAsync(process);
        return process;
    }

    private async Task FollowSenderProgressAsync(Process process, int generation)
    {
        long bytes = 0;
        long microseconds = 0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("total_size=", StringComparison.Ordinal))
                {
                    long.TryParse(line.AsSpan("total_size=".Length), out bytes);
                }
                else if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    long.TryParse(line.AsSpan("out_time_us=".Length), out microseconds);
                }
                else if (line.StartsWith("progress=", StringComparison.Ordinal) && bytes > 0 && microseconds > 0)
                {
                    var position = microseconds / 1000;
                    lock (_gate)
                    {
                        if (generation == _senderGeneration && position > Interlocked.Read(ref _publishedMilliseconds))
                        {
                            Interlocked.Exchange(ref _publishedMilliseconds, position);
                            Interlocked.Exchange(ref _lastPublishAdvanceTicks, Environment.TickCount64);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task<string> ReportSenderErrorsAsync(Process process)
    {
        try
        {
            var errors = Profile.Diagnostics
                ? await FfmpegProcessLog.ReadAsync(process.StandardError, _logger, Profile.Name, "sender", OutputUrl).ConfigureAwait(false)
                : (await process.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
            if (errors.Length > 0 && !Profile.Diagnostics)
            {
                // The tail is what matters: ffmpeg says why it stopped last.
                _logger.LogWarning(
                    "The ffmpeg sender to {Platform} said: {Errors}",
                    Profile.Platform,
                    FfmpegStreamingSession.RedactStreamKey(errors.Length > 2000 ? errors[^2000..] : errors, OutputUrl));

                // X closes the socket without an RTMP error when it refuses a publish: the end of
                // file is all ffmpeg sees, and the reason is on the side of Media Studio Producer.
                if (Profile.Platform == StreamPlatform.X && errors.Contains("End of file", StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "X closed the connection of the live: check in Media Studio Producer that the source is in region {Region}, that its key is the current one and that no broadcast on it has ended",
                        XIngestRegions.OfUrl(OutputUrl)?.Code ?? "?");
                }
            }
            return FfmpegStreamingSession.RedactStreamKey(errors, OutputUrl);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Called under the lock.</summary>
    private void CloseQuietly()
    {
        _relay?.Cancel();
        if (_sender is { } sender)
        {
            KillQuietly(sender);
            sender.Dispose();
            _sender = null;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
