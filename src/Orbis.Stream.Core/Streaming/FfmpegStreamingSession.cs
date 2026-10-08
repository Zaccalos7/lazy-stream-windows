using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming.Rtmp;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// A running live, killable on demand (the .NET equivalent of stopping the recorder). For a custom
/// ingest it is one ffmpeg. For a known platform it is the encoder and the paced relay (see
/// <see cref="FlvPacedRelay"/>), which speaks RTMP to the ingest itself - or, as a fallback, hands
/// the stream to a sender ffmpeg - all torn down together.
/// </summary>
public sealed class FfmpegStreamingSession : IAsyncDisposable
{
    /// <summary>How long the encoder may take to end on its own once the sender is gone.</summary>
    private static readonly TimeSpan EncoderGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a new encoder has to take its lead before being without one counts against it:
    /// opening the inputs and filling the lookahead is slow for everyone.
    /// </summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(10);

    private readonly long _startedTicks = Environment.TickCount64;

    private readonly Process _process;
    private readonly Process? _sender;
    private readonly FlvPacedRelay? _relay;
    private readonly RelaySegment? _segment;
    private readonly LiveOutput? _output;
    private readonly ILogger _logger;
    private readonly Task<string> _standardError;
    private readonly Task<string>? _senderError;
    private readonly Task _standardOutput;
    private readonly long _resumedFromMilliseconds;
    private readonly string _outputUrl;
    private long _positionMilliseconds;
    private long _bytesOnAir;
    private long _lastAdvanceTicks = Environment.TickCount64;
    private long _onAirSinceTicks;
    private int _stopRequested;
    private int _restartRequested;
    private int _endedNaturally;

    private FfmpegStreamingSession(
        Process process,
        Process? sender,
        FlvPacedRelay? relay,
        int videoPkid,
        string inputPath,
        MediaProbeResult probe,
        MediaOutput output,
        TimeSpan resumeFrom,
        string outputUrl,
        StreamPlatformProfile profile,
        ILogger logger,
        RelaySegment? segment = null,
        LiveOutput? liveOutput = null)
    {
        _process = process;
        _sender = sender;
        _relay = relay;
        _segment = segment;
        _output = liveOutput;
        _logger = logger;
        _outputUrl = outputUrl;
        Profile = profile;

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
        _senderError = sender is null ? null : ReadToEndAsync(sender.StandardError);

        // Nothing else reads what ffmpeg writes on its standard output, and the pipe it writes
        // <c>-progress</c> into is small: a session that left it alone would block ffmpeg itself.
        // With a relay the encoder's output is the stream, and the progress is the sender's; with
        // the native transport there is no sender, and the relay itself knows what went on air.
        // On a shared connection the standard output is the stream too, and the segment knows the rest.
        _standardOutput = IsNative || SharesOutput ? Task.CompletedTask : FollowProgressAsync(sender ?? process);
    }

    /// <summary>The relay speaks RTMP itself: what is on air is what it sent.</summary>
    private bool IsNative => _relay is not null && _sender is null;

    /// <summary>
    /// The encoder writes into the connection of the live (<see cref="LiveOutput"/>) rather than
    /// one of its own: killing it ends this pass, not the live, and the next encoder carries on
    /// the same stream.
    /// </summary>
    public bool SharesOutput => _segment is not null;

    /// <summary>The platform this live is delivered to, and how.</summary>
    public StreamPlatformProfile Profile { get; }

    /// <summary>
    /// Whether the ingest is taking the stream: the process that talks to it has written bytes to
    /// the connection and the output clock has moved. Before this a live is only a process that
    /// was started, which is what the pages used to show as LIVE whatever the platform made of it.
    /// </summary>
    public bool IsOnAir => OnAirSinceTicks != 0;

    /// <summary>How long the live has been on air; zero when it is not.</summary>
    public TimeSpan OnAirFor
    {
        get
        {
            var since = OnAirSinceTicks;
            return since == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Environment.TickCount64 - since);
        }
    }

    /// <summary>
    /// The level this pass was encoded at when the machine chose it (the setting says automatic);
    /// null when the setting names its own, which is the user's to keep. Only such a pass is
    /// adapted to the machine.
    /// </summary>
    public EncoderQuality? AdaptiveQuality { get; set; }

    /// <summary>The connection of the live this pass writes into, when it shares one.</summary>
    public LiveOutput? Connection => _output;

    /// <summary>
    /// The video bitrate of the setting this pass was started from, before the ladder of the live
    /// took anything off it: what the ladder climbs back to. Zero when the setting names none.
    /// </summary>
    public int NominalBitrate { get; private set; }

    /// <summary>The bitrate of the sound of this pass, which the network carries next to the picture.</summary>
    public int AudioBitrate { get; private set; }

    /// <summary>
    /// Whether the setting of this pass lets its bitrate follow the network (the adaptive bitrate
    /// switch of the setting, <see cref="VideoSettingAdaptiveBitrate"/>).
    /// </summary>
    public bool AdaptsBitrate { get; private set; }

    /// <summary>
    /// Reads how the connection of the live is getting through and says whether its video bitrate
    /// has to change (see <see cref="BitrateLadder"/>): null while it holds, on a live that has the
    /// connection to itself, for a setting that leaves the bitrate to the encoder, and for one
    /// whose adaptive bitrate is switched off.
    /// </summary>
    public RateDecision? AdaptBitrate() =>
        _output is { } output && AdaptsBitrate && NominalBitrate > 0 ? output.AdaptBitrate(NominalBitrate, AudioBitrate) : null;

    /// <summary>
    /// How long, past its warm up, this encoder has produced the live no faster than it goes out
    /// (<see cref="FlvPacedRelay.LowLeadSinceTicks"/>); zero while it keeps ahead, and on a live
    /// that has the connection to itself.
    /// </summary>
    public TimeSpan BehindFor
    {
        get
        {
            var since = SharesOutput ? _segment!.Relay.LowLeadSinceTicks : 0;
            if (since == 0)
            {
                return TimeSpan.Zero;
            }

            var from = Math.Max(since, _startedTicks + (long)WarmUp.TotalMilliseconds);
            var now = Environment.TickCount64;
            return now > from ? TimeSpan.FromMilliseconds(now - from) : TimeSpan.Zero;
        }
    }

    /// <summary>How long ago the position on air last moved forward.</summary>
    public TimeSpan SinceLastAdvance => TimeSpan.FromMilliseconds(
        Environment.TickCount64 - (SharesOutput
            ? _segment!.LastAdvanceTicks
            : IsNative ? _relay!.LastAdvanceTicks : Interlocked.Read(ref _lastAdvanceTicks)));

    /// <summary>
    /// Natively, on air is the ingest having accepted the publish and taken a first frame; through
    /// ffmpeg it is bytes on the connection and a clock that moved.
    /// </summary>
    private long OnAirSinceTicks => SharesOutput
        ? _segment!.OnAirSinceTicks
        : IsNative ? _relay!.OnAirSinceTicks : Interlocked.Read(ref _onAirSinceTicks);

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
    public long PositionMilliseconds => SharesOutput
        ? _resumedFromMilliseconds + _segment!.SentPositionMilliseconds
        : IsNative
            ? _resumedFromMilliseconds + _relay!.PositionMilliseconds
            : Interlocked.Read(ref _positionMilliseconds);

    /// <summary>
    /// Where the next pass carries on from when this one hands over without a break (a spot, a
    /// change of parameters, the end of the file). On a shared connection that is what the relay
    /// took from the encoder, since all of it goes on air ahead of the next one; otherwise it is
    /// what went on air, because the rest dies with the connection.
    /// </summary>
    public long ContinuationMilliseconds => SharesOutput
        ? _resumedFromMilliseconds + _segment!.ReadPositionMilliseconds
        : PositionMilliseconds;

    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    /// <summary>
    /// Why the live is being stopped when it is not the user asking: what the row says once it is.
    /// Null is the ordinary stop.
    /// </summary>
    public string? StopReason { get; set; }

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

        await KillEncoderAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the live is over. With a relay that is whatever talks to the ingest - the relay
    /// itself, or the sender ffmpeg: once it is gone nothing reaches the platform, whatever the
    /// encoder is still doing (it is cleaned up after it).
    /// </summary>
    public bool HasExited => SharesOutput
        ? _segment!.Ended
        : IsNative ? _relay!.Completion.IsCompleted : Exited(_sender ?? _process);

    private static bool Exited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
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
        string? previewPath = null,
        StreamPlatformProfile? profile = null,
        LiveOutput? output = null,
        EncoderQuality? quality = null,
        bool alwaysSound = false)
    {
        // The platform is read off the ingest; a caller may name it, which is how a relay is
        // exercised against a file on disk.
        profile ??= output?.Profile ?? StreamPlatformProfile.For(outputUrl);

        // On a shared connection of a platform that wants one format (YouTube) the first encoder
        // fixes the picture of the live and every one after it is fitted into it (see
        // LiveOutput.Frame). Elsewhere every file keeps its own.
        var own = FfmpegCommandBuilder.ResolveOutput(setting, probe);
        var frame = profile.UniformFormat ? output?.Pin(FfmpegCommandBuilder.FrameOfLive(setting, own)) : null;
        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest(
                inputPath, outputUrl, probe, setting, resumeFrom, previewPath, profile, quality, frame, BitrateOf(setting, output), alwaysSound));
        var session = Launch(locator, videoPkid, inputPath, outputUrl, arguments, probe, frame ?? own, resumeFrom, profile, logger, output);
        session.Measure(setting, profile);
        return session;
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
        TimeSpan? duration = null,
        string? previewPath = null,
        StreamPlatformProfile? profile = null,
        LiveOutput? output = null,
        EncoderQuality? quality = null,
        bool alwaysSound = false)
    {
        profile ??= output?.Profile ?? StreamPlatformProfile.For(outputUrl);

        // The composition is sent at the size of the canvas: the resolution of the setting is not
        // applied on top of it (see BuildComposition). A canvas is a size the user chose, so where
        // the platform wants one format the first one on a connection fixes the picture as it is.
        var frameRate = setting.FrameRate is > 0 ? setting.FrameRate.Value : canvasFrameRate;
        var frame = profile.UniformFormat
            ? output?.Pin(new MediaOutput(FfmpegCommandBuilder.Even(canvasWidth), FfmpegCommandBuilder.Even(canvasHeight), frameRate))
            : null;
        var arguments = FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            items, outputUrl, setting, canvasWidth, canvasHeight, canvasFrameRate, resumeFrom, duration, previewPath, profile, quality, frame,
            BitrateOf(setting, output), alwaysSound));

        var mediaOutput = frame ?? new MediaOutput(canvasWidth, canvasHeight, frameRate);
        var sound = FfmpegCommandBuilder.CarriesSound(items);

        // The length of the canvas is kept in the probe because that is where the streaming loop
        // looks for it: a playlist ends when the position reaches the length of its file, and a
        // canvas made of files ends the same way. A probe that said zero is a live that never ends.
        var probe = new MediaProbeResult(
            canvasWidth, canvasHeight, frameRate, sound, sound ? 2 : 0, duration?.TotalSeconds ?? 0);

        var session = Launch(locator, videoPkid, SceneDescriptionOf(items), outputUrl, arguments, probe, mediaOutput, resumeFrom, profile, logger, output);
        session.Measure(setting, profile);
        return session;
    }

    /// <summary>
    /// What the ladder of the live measures this pass against: the bitrate of the setting, and the
    /// one of the sound, which is the setting's or the AAC a platform that requires sound is given.
    /// A setting with the adaptive bitrate switched off is not measured at all.
    /// </summary>
    private void Measure(VideoSettingEntity setting, StreamPlatformProfile profile)
    {
        AdaptsBitrate = VideoSettingAdaptiveBitrate.IsOn(setting);
        NominalBitrate = setting.VideoBitrate is > 0 ? setting.VideoBitrate.Value : 0;
        AudioBitrate = setting.AudioSetting?.AudioBitrate is int audio and > 0
            ? audio
            : setting.AudioSetting is not null || profile.RequiresAudio ? 128_000 : 0;
    }

    /// <summary>
    /// The video bitrate a pass goes out at: under the ladder of the live when the setting lets the
    /// bitrate follow the network, the bitrate of the setting itself when it does not (null).
    /// </summary>
    internal static int? BitrateOf(VideoSettingEntity setting, LiveOutput? output) =>
        VideoSettingAdaptiveBitrate.IsOn(setting) ? output?.Ladder.BitrateFor(setting.VideoBitrate) : null;

    private static FfmpegStreamingSession Launch(
        FfmpegToolLocator locator,
        int videoPkid,
        string inputPath,
        string outputUrl,
        IReadOnlyList<string> arguments,
        MediaProbeResult probe,
        MediaOutput output,
        TimeSpan resumeFrom,
        StreamPlatformProfile profile,
        ILogger logger,
        LiveOutput? liveOutput = null)
    {
        if (liveOutput is not null && profile.UsesRelay)
        {
            var sharedEncoder = StartProcess(locator, arguments, redirectInput: false, inputPath);
            RelaySegment segment;
            try
            {
                segment = liveOutput.Attach(sharedEncoder.StandardOutput.BaseStream);
            }
            catch
            {
                KillQuietly(sharedEncoder);
                sharedEncoder.Dispose();
                throw;
            }

            logger.LogInformation(
                "ffmpeg started for {Input}, on the open connection of the live to {Platform}",
                inputPath,
                profile.Platform);
            return new FfmpegStreamingSession(
                sharedEncoder, null, null, videoPkid, inputPath, probe, output, resumeFrom, outputUrl, profile, logger, segment, liveOutput);
        }

        if (profile.UsesRelay && profile.Transport == RelayTransport.NativeRtmp)
        {
            var nativeEncoder = StartProcess(locator, arguments, redirectInput: false, inputPath);
            var publisher = new RtmpPublisher(outputUrl, profile.ConnectTimeout, profile.StallTimeout, logger);
            var nativeRelay = new FlvPacedRelay(nativeEncoder.StandardOutput.BaseStream, publisher, profile, logger);
            nativeRelay.Start();

            logger.LogInformation(
                "ffmpeg started for {Input} -> {OutputUrl}, paced and published over RTMP to {Platform}",
                inputPath,
                RedactStreamKey(outputUrl, outputUrl),
                profile.Platform);
            return new FfmpegStreamingSession(
                nativeEncoder, null, nativeRelay, videoPkid, inputPath, probe, output, resumeFrom, outputUrl, profile, logger);
        }

        if (!profile.UsesRelay)
        {
            var process = StartProcess(locator, arguments, redirectInput: false, inputPath);

            // The output is named, not read off the end of the command line: the preview comes after it.
            logger.LogInformation(
                "ffmpeg started for {Input} -> {OutputUrl}", inputPath, RedactStreamKey(outputUrl, outputUrl));
            return new FfmpegStreamingSession(
                process, null, null, videoPkid, inputPath, probe, output, resumeFrom, outputUrl, profile, logger);
        }

        // The sender first, so it is already reading when the first frame comes through the relay.
        var sender = StartProcess(locator, FfmpegCommandBuilder.BuildSender(outputUrl, profile), redirectInput: true, inputPath);
        Process encoder;
        try
        {
            encoder = StartProcess(locator, arguments, redirectInput: false, inputPath);
        }
        catch
        {
            KillQuietly(sender);
            sender.Dispose();
            throw;
        }

        var relay = new FlvPacedRelay(encoder.StandardOutput.BaseStream, sender.StandardInput.BaseStream, profile, logger);
        relay.Start();

        logger.LogInformation(
            "ffmpeg started for {Input} -> {OutputUrl} through the {Platform} relay",
            inputPath,
            RedactStreamKey(outputUrl, outputUrl),
            profile.Platform);
        return new FfmpegStreamingSession(
            encoder, sender, relay, videoPkid, inputPath, probe, output, resumeFrom, outputUrl, profile, logger);
    }

    private static Process StartProcess(
        FfmpegToolLocator locator, IReadOnlyList<string> arguments, bool redirectInput, string inputPath)
    {
        var startInfo = locator.CreateStartInfo(locator.FfmpegPath, arguments);
        startInfo.RedirectStandardInput = redirectInput;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start ffmpeg for {inputPath}");
        }

        return process;
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
    /// why it is not the one used here. <c>total_size</c> is what was written to the first output -
    /// the ingest - and it stays N/A until the connection to it is open: bytes there and a clock
    /// that moves are what make a live on air rather than a process that was started.
    /// </summary>
    private async Task FollowProgressAsync(Process process)
    {
        const string timeKey = "out_time_us=";
        const string sizeKey = "total_size=";

        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (TryRead(line, timeKey, out var microseconds) && microseconds >= 0)
                {
                    var position = _resumedFromMilliseconds + microseconds / 1000;
                    if (position > Interlocked.Exchange(ref _positionMilliseconds, position))
                    {
                        Interlocked.Exchange(ref _lastAdvanceTicks, Environment.TickCount64);
                    }

                    if (microseconds > 0 && Interlocked.Read(ref _bytesOnAir) > 0)
                    {
                        Interlocked.CompareExchange(ref _onAirSinceTicks, Environment.TickCount64, 0);
                    }
                }
                else if (TryRead(line, sizeKey, out var bytes))
                {
                    Interlocked.Exchange(ref _bytesOnAir, bytes);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // The process was killed or disposed: the last position reported is the one that counts.
        }
    }

    private static bool TryRead(string line, string key, out long value)
    {
        value = 0;
        return line.StartsWith(key, StringComparison.Ordinal)
            && long.TryParse(
                line.AsSpan(key.Length),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    /// <summary>
    /// The exit code of the live. With a relay the sender says whether the ingest took the stream;
    /// once it is gone the encoder is given a moment to end (it ends by itself when the file did)
    /// and killed otherwise, since nothing reads what it writes any more. A sender that ended
    /// cleanly on an encoder that failed - or that had to be killed, still running with nobody
    /// to send to - is the encoder's failure: the sender only ran out of input.
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (SharesOutput)
        {
            return await WaitForSegmentExitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsNative)
        {
            return await WaitForNativeExitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_sender is null)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        await _sender.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            grace.CancelAfter(EncoderGrace);
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillQuietly(_process);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return _sender.ExitCode != 0 ? _sender.ExitCode : _process.ExitCode;
    }

    /// <summary>
    /// The native live is over when the relay is: a relay that completed handed the whole stream
    /// over and closed the publish, and the encoder's exit code says whether the source was read
    /// to the end. A relay that failed - the ingest refused or dropped the stream - is a failure
    /// whatever the encoder did, and the encoder is killed since nobody reads it any more.
    /// </summary>
    private async Task<int> WaitForNativeExitAsync(CancellationToken cancellationToken)
    {
        var relay = _relay!;
        await relay.Completion.ContinueWith(static _ => { }, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default)
            .ConfigureAwait(false);

        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            grace.CancelAfter(EncoderGrace);
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillQuietly(_process);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!relay.Completion.IsCompletedSuccessfully)
        {
            return _process.ExitCode != 0 ? _process.ExitCode : 1;
        }

        return _process.ExitCode;
    }

    /// <summary>
    /// A pass on a shared connection is over when the relay took the last of its encoder, or when
    /// the connection is gone. The connection breaking is a failure whatever the encoder did; the
    /// encoder ending on its own is its exit code.
    /// </summary>
    private async Task<int> WaitForSegmentExitAsync(CancellationToken cancellationToken)
    {
        var segment = _segment!;
        await Task.WhenAny(segment.ReadCompletion, segment.Relay.Completion).WaitAsync(cancellationToken).ConfigureAwait(false);

        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            grace.CancelAfter(EncoderGrace);
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillQuietly(_process);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (segment.Relay.Completion.IsCompleted && !segment.Relay.Completion.IsCompletedSuccessfully)
        {
            return _process.ExitCode != 0 ? _process.ExitCode : 1;
        }

        return _process.ExitCode;
    }

    /// <summary>
    /// What ffmpeg wrote on its standard error, with the stream key masked: ffmpeg names the output
    /// url in every error about it, and this text ends up on the live history page and in the log,
    /// where a key in clear is a key anyone looking at the screen can stream with. With a relay the
    /// two processes speak in turn: the sender about the ingest first, since that is where a live
    /// usually breaks, then the encoder about the source.
    /// </summary>
    public async Task<string> ReadErrorAsync()
    {
        var encoder = await _standardError.ConfigureAwait(false);
        if (IsNative || SharesOutput)
        {
            // The ingest's own words (a refused publish, a dropped connection) come first.
            var connection = SharesOutput ? _segment!.Relay : _relay!;
            var relay = connection.Completion.Exception?.GetBaseException().Message ?? string.Empty;
            return RedactStreamKey(
                string.Join("\n", new[] { relay.Trim(), encoder.Trim() }.Where(text => text.Length > 0)), _outputUrl);
        }

        if (_senderError is null)
        {
            return RedactStreamKey(encoder, _outputUrl);
        }

        var sender = await _senderError.ConfigureAwait(false);
        var both = string.Join(
            "\n", new[] { sender.Trim(), encoder.Trim() }.Where(text => text.Length > 0));
        return RedactStreamKey(both, _outputUrl);
    }

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

        await KillAllAsync().ConfigureAwait(false);
        _output?.Abort();
        _logger.LogInformation("ffmpeg stopped for {Input}", InputPath);
    }

    /// <summary>
    /// Kills a live that is broken (it stalled, or the ingest never took it) without calling it a
    /// stop or an end: the caller decides whether it is reconnected or reported. A connection that
    /// is not moving is closed with it, so a reconnection starts from a fresh publish.
    /// </summary>
    public async Task AbortAsync()
    {
        await KillAllAsync().ConfigureAwait(false);
        _output?.Abort();
    }

    /// <summary>
    /// Ends this pass so that another one takes its place: on a shared connection only the encoder
    /// goes, and what it already handed to the relay still goes on air ahead of the next one. Read
    /// <see cref="ContinuationMilliseconds"/> after this, when the encoder can no longer move it.
    /// </summary>
    public async Task HandOverAsync()
    {
        await KillEncoderAsync().ConfigureAwait(false);
        if (_segment is not null)
        {
            await _segment.ReadCompletion.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The encoder alone on a shared connection, everything on a connection of its own: the
    /// connection of the live belongs to the live, not to one of its passes.
    /// </summary>
    private Task KillEncoderAsync()
    {
        if (!SharesOutput)
        {
            return KillAllAsync();
        }

        _segment!.Detach();
        KillQuietly(_process);
        return WaitQuietlyAsync(_process);
    }

    private static async Task WaitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// The encoder first, so nothing new enters the relay, then the relay, then the sender: the
    /// connection to the ingest is the last thing to go.
    /// </summary>
    private async Task KillAllAsync()
    {
        _segment?.Detach();
        KillQuietly(_process);

        // Natively this also closes the RTMP connection, which unblocks a write stuck on a full
        // send buffer: the relay thread ends instead of waiting on a network that is gone.
        _relay?.Cancel();
        if (_sender is not null)
        {
            KillQuietly(_sender);
        }

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            if (_sender is not null)
            {
                await _sender.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Never started, or already disposed: there is nothing left to wait for.
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
            // The process already exited between the check and the kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await KillEncoderAsync().ConfigureAwait(false);
        }
        finally
        {
            // The readers end on their own once the pipes are gone, and none of them can fault:
            // waiting for them releases the last reference to the processes before they are disposed.
            await Task.WhenAll(_standardError, _senderError ?? Task.FromResult(string.Empty), _standardOutput)
                .ConfigureAwait(false);
            if (_relay is not null)
            {
                await _relay.Completion.ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }

            _process.Dispose();
            _sender?.Dispose();
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
