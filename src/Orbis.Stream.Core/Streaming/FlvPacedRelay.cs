using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The frame loop of the Java version, moved after the encoder: the encoder writes FLV on its
/// standard output, this reads it one tag at a time and hands every tag to the sink at the moment
/// its timestamp says, measured against the clock the first tag was sent on. That is
/// <c>target = start + pts</c>, the park and spin of <see cref="HybridWaiter"/> and a write, for
/// every frame, exactly as Java did around <c>recorder.record</c>.
/// <para>Pacing the encoded stream rather than the decoded picture is what makes it light and
/// steady: what crosses into .NET is a compressed packet, not a raw frame, and a slow frame of
/// the encoder no longer delays the frame on air, because the encoder runs up to
/// <see cref="StreamPlatformProfile.MaxLead"/> ahead and what it already produced is sent on time
/// while it catches up. The bound is what keeps it honest: once the queue holds that much, the
/// reader stops reading, the pipe fills, and the encoder waits on its own write.</para>
/// <para>Two threads: one reads (it may block on the encoder whenever it likes), one sends (it
/// must never be late because of a read). The sending one runs above normal priority, so it is
/// awake when a frame is due.</para>
/// </summary>
public sealed class FlvPacedRelay
{
    private const int FlvHeaderMinimum = 9;
    private const int PreviousTagSizeSize = 4;
    private const byte ScriptTag = 18;

    /// <summary>A bound on the queue in tags, for a stream whose timestamps stop moving.</summary>
    private const int MaxQueuedTags = 4096;

    /// <summary>
    /// How far in the future the next tag may be due before it is a jump of the timestamps rather
    /// than a frame to wait for. Consecutive tags are a frame apart, a second at most on the
    /// sparsest stream; what is further ahead is a discontinuity - above all the sequence headers
    /// ffmpeg stamps 0 in front of a stream whose frames start later - and waiting for it would
    /// hold the live for as long as the jump is.
    /// </summary>
    private static readonly TimeSpan MaxForwardJump = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much faster than real time the relay sends while it has a backlog to win back. Two is
    /// enough to drain the jitter buffer and leave again without flooding an uplink that has just
    /// been the reason for the lag.
    /// </summary>
    private const int CatchUpRate = 2;

    private readonly System.IO.Stream _source;
    private readonly IFlvSink _sink;
    private readonly StreamPlatformProfile _profile;
    private readonly ILogger _logger;
    private readonly Queue<FlvTag> _queue = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[]? _header;
    private bool _sourceEnded;
    private long _lastQueuedTimestamp;
    private long _lastSentTimestamp;
    private long _sentTimestamp = -1;
    private long _lastMediaTimestamp = -1;
    private long _positionMilliseconds;
    private long _onAirSinceTicks;
    private long _lastAdvanceTicks = Environment.TickCount64;
    private int _rebases;
    private int _catchingUp;
    private long _worstLateMilliseconds;

    public FlvPacedRelay(System.IO.Stream source, IFlvSink sink, StreamPlatformProfile profile, ILogger logger)
    {
        _source = source;
        _sink = sink;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>The relay to a stream of FLV bytes: an ffmpeg sender, a file, a test.</summary>
    public FlvPacedRelay(System.IO.Stream source, System.IO.Stream destination, StreamPlatformProfile profile, ILogger logger)
        : this(source, new StreamFlvSink(destination), profile, logger)
    {
    }

    /// <summary>Completes once the last tag was handed over and the sink was closed; faults with why it could not.</summary>
    public Task Completion => _completion.Task;

    /// <summary>The timestamp of the last tag handed to the sink, in milliseconds; -1 before the first.</summary>
    public long SentTimestampMilliseconds => Interlocked.Read(ref _sentTimestamp);

    /// <summary>
    /// How much of the stream went on air, in milliseconds: the timestamps of the frames sent,
    /// counted from the first and without the jumps the clock followed, so a live whose encoder
    /// stamps from an offset, or that jumped, still resumes from the point it really got to.
    /// </summary>
    public long PositionMilliseconds => Interlocked.Read(ref _positionMilliseconds);

    /// <summary>When the first frame was handed to an open sink (Environment.TickCount64); 0 before.</summary>
    public long OnAirSinceTicks => Interlocked.Read(ref _onAirSinceTicks);

    /// <summary>When the timestamp on air last moved forward (Environment.TickCount64).</summary>
    public long LastAdvanceTicks => Interlocked.Read(ref _lastAdvanceTicks);

    /// <summary>How many times the clock was moved because the network fell too far behind.</summary>
    public int Rebases => Volatile.Read(ref _rebases);

    /// <summary>
    /// How many times the relay fell a second behind and had to send faster than real time to get
    /// back on schedule. A number that keeps climbing is a live that is losing more than its
    /// jitter buffer holds, which is what a viewer sees as buffering.
    /// </summary>
    public int CatchingUp => Volatile.Read(ref _catchingUp);

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "orbis-relay-read" }.Start();
        new Thread(SendLoop) { IsBackground = true, Name = "orbis-relay-send", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>Stops both loops and closes the sink at once; whatever is queued is dropped.</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _sink.Abort();
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    private void ReadLoop()
    {
        try
        {
            var header = ReadHeader();
            lock (_gate)
            {
                _header = header;
                Monitor.PulseAll(_gate);
            }

            while (!_cancellation.IsCancellationRequested && ReadTag() is { } tag)
            {
                lock (_gate)
                {
                    // The bound of the jitter buffer: past it the reader waits for the sender, the
                    // pipe fills, and the encoder waits on its write. That is the backpressure that
                    // keeps it no more than MaxLead ahead of what is on air.
                    while (!_cancellation.IsCancellationRequested
                        && _queue.Count > 0
                        && (_lastQueuedTimestamp - _lastSentTimestamp > Milliseconds(_profile.MaxLead)
                            || _queue.Count >= MaxQueuedTags))
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_cancellation.IsCancellationRequested)
                    {
                        tag.Release();
                        break;
                    }

                    _queue.Enqueue(tag);
                    _lastQueuedTimestamp = Math.Max(_lastQueuedTimestamp, tag.Timestamp);
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException)
        {
            // The encoder went away mid tag or wrote something that is not FLV: what was queued
            // is still sent, and the exit code of the encoder tells the rest.
            _logger.LogDebug(exception, "The relay stopped reading the encoder");
        }
        finally
        {
            lock (_gate)
            {
                _sourceEnded = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void SendLoop()
    {
        var token = _cancellation.Token;
        var completed = false;
        try
        {
            using var waiter = HybridWaiter.Create();
            _logger.LogInformation(
                "Relay to {Platform}: preroll {Preroll}s, lead {Lead}s, {Sleeper} wakes {Overshoot:0.###} ms late",
                _profile.Platform,
                _profile.Preroll.TotalSeconds,
                _profile.MaxLead.TotalSeconds,
                waiter.SleeperName,
                waiter.SleepOvershoot.TotalMilliseconds);

            // The connection is opened while the encoder starts: neither waits for the other, and
            // the reader queues the first second of the live meanwhile.
            _sink.Open(token);

            if (WaitForHeader() is not { } header)
            {
                _sink.Complete();
                completed = true;
                return;
            }

            _sink.WriteHeader(header);

            long? anchor = null;
            long baseTimestamp = 0;

            // How much of the stream still has to be won back after a slow moment, in stopwatch
            // ticks. Only the sending thread touches it.
            var recovery = 0L;
            var maxForwardJump = HybridWaiter.Ticks(MaxForwardJump);
            var maxLead = HybridWaiter.Ticks(_profile.MaxLead);

            while (NextTag() is { } tag)
            {
                try
                {
                    if (tag.Type != ScriptTag)
                    {
                        // The clock starts on the first frame, already Preroll in the past:
                        // everything stamped before Preroll is due at once and goes out in a burst.
                        if (anchor is null)
                        {
                            anchor = HybridWaiter.Now - HybridWaiter.Ticks(_profile.Preroll);
                            baseTimestamp = tag.Timestamp;
                        }

                        // targetTimeNanos = startTimeNanos + framePts, as in Java.
                        var target = anchor.Value + HybridWaiter.Ticks(TimeSpan.FromMilliseconds(tag.Timestamp - baseTimestamp));
                        var late = HybridWaiter.Now - target;

                        // The preroll is sent early on purpose: it is due before the clock the
                        // anchor sets, and it is not a late frame. Only what is late after the
                        // preroll counts, as a shortfall or in the log.
                        var inPreroll = tag.Timestamp - baseTimestamp < Milliseconds(_profile.Preroll);
                        if (!inPreroll)
                        {
                            _worstLateMilliseconds = Math.Max(
                                _worstLateMilliseconds, late * 1000 / System.Diagnostics.Stopwatch.Frequency);
                        }

                        if (-late > maxForwardJump)
                        {
                            // A jump forward of the timestamps: the clock follows it, and this tag is
                            // due now instead of when the jump says.
                            anchor += late;
                            Interlocked.Increment(ref _rebases);
                            _logger.LogInformation(
                                "The timestamps of the live to {Platform} jumped {Jump:0.0}s ahead: the relay clock followed",
                                _profile.Platform,
                                -late / (double)System.Diagnostics.Stopwatch.Frequency);
                        }
                        else
                        {
                            // Every frame waits for the wall clock its timestamps say, whether it
                            // early or late: `late` here is how far past that moment we are, and it
                            // is worked off below rather than waited on. The preroll is not a
                            // shortfall: it is what was held back to be sent in one burst.
                            if (!inPreroll)
                            {
                                recovery = Math.Min(recovery + Math.Max(0, late), maxLead);
                            }

                            if (recovery >= HybridWaiter.Ticks(TimeSpan.FromSeconds(1)))
                            {
                                Interlocked.Increment(ref _catchingUp);
                                _logger.LogWarning(
                                    "The live to {Platform} fell {Late:0.0}s behind: sending at {Rate}x to win it back",
                                    _profile.Platform,
                                    recovery / (double)System.Diagnostics.Stopwatch.Frequency,
                                    CatchUpRate);
                            }

                            // A frame on time waits its whole moment: the pacing of a live that is keeping up is
                            // untouched. Only a live that is behind is sent fast, and then the gap
                            // is shortened by 1/CatchUpRate, so the frames go out CatchUpRate
                            // times faster than real time and the shortfall is made up over the
                            // frames the jitter buffer already holds. Nothing else changes: no
                            // clock is moved, no timestamp is rewritten, and the ingest keeps
                            // seeing a contiguous timeline.
                            var full = target - HybridWaiter.Now;
                            if (full > 0)
                            {
                                // What the frame is owed is `full`; what the catch-up takes off it
                                // is at most the shortfall, so the faster sending never runs past
                                // zero and turns into a burst, and never exceeds twice the rate.
                                var spared = recovery > 0 ? Math.Min(full, recovery / CatchUpRate) : 0;
                                waiter.WaitUntil(HybridWaiter.Now + full - spared, token);

                                // The shortfall is reduced by what was actually gained, which is the
                                // time not waited on: sending a frame early is as much of a gain as
                                // waiting less for the next one.
                                recovery = Math.Max(0, recovery - spared);
                            }
                            else
                            {
                                // Already past due, so this frame costs no wall clock at all: the
                                // whole of its moment is gained back, up to what is owed.
                                recovery = Math.Max(0, recovery - Math.Min(-full, maxLead));
                            }
                        }
                    }

                    _sink.WriteTag(tag);
                    MarkSent(tag);
                }
                finally
                {
                    tag.Release();
                }
            }

            _logger.LogInformation(
                "Relay to {Platform} finished: worst lateness {Late} ms, {Rebases} clock moves, {CatchingUp} catch-ups",
                _profile.Platform,
                _worstLateMilliseconds,
                Rebases,
                CatchingUp);
            _sink.Complete();
            completed = true;
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetCanceled(token);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The ingest closed the connection, refused the publish, or the sender process died:
            // the message says which, and the live is reconnected or reported from it.
            _logger.LogWarning("The relay to {Platform} stopped: {Reason}", _profile.Platform, exception.Message);
            _completion.TrySetException(exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The relay to {Platform} failed", _profile.Platform);
            _completion.TrySetException(exception);
        }
        finally
        {
            if (completed)
            {
                _completion.TrySetResult();
            }

            // Whatever ended the loop, the reader stops, the queue goes back to the pool and the
            // sink is closed (it already is after a Complete, and Abort is harmless then).
            _completion.TrySetCanceled();
            Cancel();
            DrainQueue();
        }
    }

    /// <summary>Only the sending thread writes these; the session reads them from its monitor.</summary>
    private void MarkSent(FlvTag tag)
    {
        var now = Environment.TickCount64;
        if (tag.Timestamp > Interlocked.Read(ref _sentTimestamp))
        {
            Interlocked.Exchange(ref _sentTimestamp, tag.Timestamp);
        }

        if (tag.Type == ScriptTag)
        {
            return;
        }

        Interlocked.CompareExchange(ref _onAirSinceTicks, now, 0);

        // A step of the timestamps is media time on air; a step past the jump bound is a
        // discontinuity the clock followed, and it adds nothing.
        var step = _lastMediaTimestamp < 0 ? 0 : tag.Timestamp - _lastMediaTimestamp;
        _lastMediaTimestamp = Math.Max(_lastMediaTimestamp, tag.Timestamp);
        if (step > 0 && step <= (long)MaxForwardJump.TotalMilliseconds)
        {
            Interlocked.Add(ref _positionMilliseconds, step);
            Interlocked.Exchange(ref _lastAdvanceTicks, now);
        }
    }

    private void DrainQueue()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var tag))
            {
                tag.Release();
            }
        }
    }

    private byte[]? WaitForHeader()
    {
        lock (_gate)
        {
            while (_header is null && !_sourceEnded && !_cancellation.IsCancellationRequested)
            {
                Monitor.Wait(_gate);
            }

            return _header;
        }
    }

    /// <summary>The next tag to send, or null when the encoder is done and the queue is empty.</summary>
    private FlvTag? NextTag()
    {
        lock (_gate)
        {
            while (_queue.Count == 0 && !_sourceEnded && !_cancellation.IsCancellationRequested)
            {
                Monitor.Wait(_gate);
            }

            _cancellation.Token.ThrowIfCancellationRequested();
            if (_queue.Count == 0)
            {
                return null;
            }

            var tag = _queue.Dequeue();
            _lastSentTimestamp = Math.Max(_lastSentTimestamp, tag.Timestamp);
            Monitor.PulseAll(_gate);
            return tag;
        }
    }

    /// <summary>The FLV header and the PreviousTagSize0 after it, forwarded as they were.</summary>
    private byte[] ReadHeader()
    {
        var start = new byte[FlvHeaderMinimum];
        if (!ReadExactly(start))
        {
            throw new InvalidDataException("The encoder ended before writing an FLV header");
        }

        if (start[0] != 'F' || start[1] != 'L' || start[2] != 'V')
        {
            throw new InvalidDataException("The encoder did not write FLV");
        }

        var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(5));
        if (dataOffset < FlvHeaderMinimum || dataOffset > 1024)
        {
            throw new InvalidDataException($"FLV header of {dataOffset} bytes");
        }

        var header = new byte[dataOffset + PreviousTagSizeSize];
        start.CopyTo(header, 0);
        if (!ReadExactly(header.AsSpan(FlvHeaderMinimum)))
        {
            throw new InvalidDataException("The encoder ended inside the FLV header");
        }

        return header;
    }

    /// <summary>One whole tag with its trailing PreviousTagSize, or null at the end of the stream.</summary>
    private FlvTag? ReadTag()
    {
        Span<byte> head = stackalloc byte[FlvTag.HeaderSize];
        if (!ReadExactly(head))
        {
            return null;
        }

        var dataSize = (head[1] << 16) | (head[2] << 8) | head[3];
        var timestamp = ((long)head[7] << 24) | ((long)head[4] << 16) | ((long)head[5] << 8) | head[6];
        var length = FlvTag.HeaderSize + dataSize + FlvTag.TrailerSize;

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        head.CopyTo(buffer);
        if (!ReadExactly(buffer.AsSpan(FlvTag.HeaderSize, dataSize + FlvTag.TrailerSize)))
        {
            // A tag cut in half is not sent: the ingest would choke on it.
            ArrayPool<byte>.Shared.Return(buffer);
            return null;
        }

        return new FlvTag((byte)(head[0] & 0x1F), timestamp, buffer, length);
    }

    private bool ReadExactly(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _source.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static long Milliseconds(TimeSpan duration) => (long)duration.TotalMilliseconds;
}
