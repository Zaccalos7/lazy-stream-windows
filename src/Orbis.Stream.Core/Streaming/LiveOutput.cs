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
    public static LiveOutput? For(string outputUrl, FfmpegToolLocator locator, ILogger logger, RelayTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(outputUrl);
        var profile = StreamPlatformProfile.For(outputUrl);
        if (!profile.UsesRelay)
        {
            return null;
        }

        return new LiveOutput(outputUrl, profile.WithTransport(transport), locator, logger);
    }

    public string OutputUrl { get; }

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
            "Connection to {Platform} opened for the whole live, published by {Transport}, paced by {Pacing}: {OutputUrl}",
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

        // Nobody reads the progress of the sender here, and a pipe nobody reads is a process that
        // blocks on its next write. Its errors are kept: the sender is the one that talks to the
        // ingest, so when the connection breaks its last words are the only account of why.
        _ = process.StandardOutput.BaseStream.CopyToAsync(System.IO.Stream.Null);
        _ = ReportSenderErrorsAsync(process);
        return process;
    }

    private async Task ReportSenderErrorsAsync(Process process)
    {
        try
        {
            var errors = (await process.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
            if (errors.Length > 0)
            {
                // The tail is what matters: ffmpeg says why it stopped last.
                _logger.LogWarning(
                    "The ffmpeg sender to {Platform} said: {Errors}",
                    Profile.Platform,
                    FfmpegStreamingSession.RedactStreamKey(errors.Length > 2000 ? errors[^2000..] : errors, OutputUrl));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
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
