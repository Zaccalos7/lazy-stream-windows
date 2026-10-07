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
/// <para>What it does when it <em>is</em> late is the part a viewer feels. A slow moment of the
/// network is a gap to win back, not a gap to drop: the clock the timestamps are measured against
/// is never moved for lateness and no timestamp is ever rewritten, so the ingest always gets one
/// contiguous timeline. The frames simply go out <see cref="CatchUpRate"/> times faster than real
/// time until the live is even again, paced rather than in a burst, and it costs nothing when
/// there is nothing to win back.</para>
/// </summary>
public sealed class FlvPacedRelay
{
    private const int FlvHeaderMinimum = 9;
    private const int PreviousTagSizeSize = 4;
    private const byte VideoTag = 9;
    internal const byte ScriptTag = 18;

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

    /// <summary>
    /// The step a segment is laid after the one before it at when nothing better is known: one
    /// frame at 30 fps. The step the video of the stream really has replaces it at the first pair
    /// of frames.
    /// </summary>
    private const long DefaultFrameStep = 33;

    private readonly Queue<RelaySegment> _segments = new();
    private readonly IFlvSink _sink;
    private readonly StreamPlatformProfile _profile;
    private readonly ILogger _logger;
    private readonly Queue<QueuedTag> _queue = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[]? _header;
    private bool _sourceEnded;
    private bool _closed;
    private bool _anyTagQueued;
    private long _lastVideoTimestamp = -1;
    private long _frameStep = DefaultFrameStep;
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
        : this(sink, profile, logger)
    {
        // One encoder for the whole connection: it is the only segment there will ever be.
        _segments.Enqueue(new RelaySegment(this, source));
        _closed = true;
    }

    /// <summary>
    /// A relay that outlives its encoders: one connection to the ingest for the whole live, fed by
    /// one encoder after the other (<see cref="Attach"/>) until <see cref="Close"/>. The ingest sees
    /// a single publish and a single timeline, so going from a video to a spot and back is not a
    /// live that ends and starts again, which is what the platforms show as the stream going off
    /// air.
    /// </summary>
    public FlvPacedRelay(IFlvSink sink, StreamPlatformProfile profile, ILogger logger)
    {
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

    /// <summary>
    /// Queues the output of one more encoder behind the ones already attached. Its timestamps are
    /// moved to carry on one frame after the last tag of the stream, its FLV header and its
    /// metadata are dropped (the ingest has them already), and its sequence headers go through, so
    /// a decoder on the other side reconfigures itself if the new encoder differs from the old one.
    /// </summary>
    public RelaySegment Attach(System.IO.Stream source)
    {
        lock (_gate)
        {
            if (_closed || _cancellation.IsCancellationRequested || _completion.Task.IsCompleted)
            {
                throw new InvalidOperationException("The relay is not taking encoders any more");
            }

            var segment = new RelaySegment(this, source);
            _segments.Enqueue(segment);
            Monitor.PulseAll(_gate);
            return segment;
        }
    }

    /// <summary>Whether a new encoder can still be attached: the connection is open and not closing.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_closed && !_cancellation.IsCancellationRequested && !_completion.Task.IsCompleted;
            }
        }
    }

    /// <summary>No more encoders: what is queued is sent, then the connection is closed the way the ingest expects.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stops reading a segment at the next tag boundary; the caller kills its encoder.</summary>
    internal void Detach(RelaySegment segment)
    {
        lock (_gate)
        {
            segment.Detached = true;
            Monitor.PulseAll(_gate);
        }
    }

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
            while (NextSegment() is { } segment)
            {
                ReadSegment(segment);
            }
        }
        finally
        {
            lock (_gate)
            {
                _sourceEnded = true;
                foreach (var pending in _segments)
                {
                    pending.MarkReadEnded();
                }

                _segments.Clear();
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>The next encoder to read, or null once the relay was closed with none left, or cancelled.</summary>
    private RelaySegment? NextSegment()
    {
        lock (_gate)
        {
            while (_segments.Count == 0 && !_closed && !_cancellation.IsCancellationRequested)
            {
                Monitor.Wait(_gate);
            }

            return _cancellation.IsCancellationRequested || _segments.Count == 0 ? null : _segments.Dequeue();
        }
    }

    private void ReadSegment(RelaySegment segment)
    {
        try
        {
            var header = ReadHeader(segment.Source);
            lock (_gate)
            {
                // The first encoder's header opens the stream; the ones after it are already inside it.
                if (_header is null)
                {
                    _header = header;
                    Monitor.PulseAll(_gate);
                }
            }

            long? offset = null;
            while (!_cancellation.IsCancellationRequested && !segment.Detached && ReadTag(segment.Source) is { } tag)
            {
                // The metadata of an encoder after the first describes a stream the ingest is not
                // being sent: the publish has one, the one it opened with.
                if (tag.Type == ScriptTag && _anyTagQueued)
                {
                    tag.Release();
                    continue;
                }

                var original = tag.Timestamp;
                if (tag.Type != ScriptTag)
                {
                    // The first frame of the segment lands one frame after the last tag of the
                    // stream, and everything after it keeps its distance from that frame. The first
                    // segment keeps its own timestamps, which is the relay of one encoder as it was.
                    offset ??= _anyTagQueued ? _lastQueuedTimestamp + _frameStep - tag.Timestamp : 0;
                    if (offset.Value != 0)
                    {
                        tag.Restamp(Math.Max(0, tag.Timestamp + offset.Value));
                    }
                }

                lock (_gate)
                {
                    // The bound of the jitter buffer: past it the reader waits for the sender, the
                    // pipe fills, and the encoder waits on its write. That is the backpressure that
                    // keeps it no more than MaxLead ahead of what is on air.
                    while (!_cancellation.IsCancellationRequested
                        && !segment.Detached
                        && _queue.Count > 0
                        && (_lastQueuedTimestamp - _lastSentTimestamp > Milliseconds(_profile.MaxLead)
                            || _queue.Count >= MaxQueuedTags))
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_cancellation.IsCancellationRequested || segment.Detached)
                    {
                        // What this segment read past the point it was let go at never goes on air,
                        // so it is not part of where it got to either.
                        tag.Release();
                        break;
                    }

                    if (tag.Type == VideoTag)
                    {
                        if (_lastVideoTimestamp >= 0 && tag.Timestamp - _lastVideoTimestamp is > 0 and < 1000 and var step)
                        {
                            _frameStep = step;
                        }

                        _lastVideoTimestamp = tag.Timestamp;
                    }

                    if (tag.Type != ScriptTag)
                    {
                        segment.MarkRead(original);
                    }

                    _queue.Enqueue(new QueuedTag(tag, segment));
                    _anyTagQueued = true;
                    _lastQueuedTimestamp = Math.Max(_lastQueuedTimestamp, tag.Timestamp);
                    segment.MarkQueued();
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
            segment.MarkReadEnded();
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
                "Relay to {Platform}, paced by {Pacing}: preroll {Preroll}s, lead {Lead}s, {Sleeper} wakes {Overshoot:0.###} ms late",
                _profile.Platform,
                _profile.Pacing == RelayPacing.Relay ? "the relay" : "the ffmpeg sender",
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
            var lastTarget = 0L;

            // When the catch-up under way began (stopwatch ticks); null while the live is on time.
            long? catchingUpSince = null;

            var maxForwardJump = HybridWaiter.Ticks(MaxForwardJump);
            var maxLead = HybridWaiter.Ticks(_profile.MaxLead);
            var lateEnough = HybridWaiter.Ticks(TimeSpan.FromSeconds(1));

            // Paced by the sender, the relay hands every tag over as soon as the sender takes it:
            // the pipe fills at the rate the sender reads, and that is the backpressure the jitter
            // buffer is measured against. A second clock here would only fight the one there.
            var paced = _profile.Pacing == RelayPacing.Relay;

            while (NextTag() is { } queued)
            {
                var tag = queued.Tag;
                try
                {
                    if (paced && tag.Type != ScriptTag)
                    {
                        // The clock starts on the first frame, already Preroll in the past:
                        // everything stamped before Preroll is due at once and goes out in a burst.
                        if (anchor is null)
                        {
                            anchor = HybridWaiter.Now - HybridWaiter.Ticks(_profile.Preroll);
                            baseTimestamp = tag.Timestamp;

                            // The first frame is due now and leaves no wait behind it: the frame
                            // after it is the first one the gap between frames measures.
                            lastTarget = anchor.Value;
                        }

                        // targetTimeNanos = startTimeNanos + framePts, as in Java.
                        var target = anchor.Value + HybridWaiter.Ticks(TimeSpan.FromMilliseconds(tag.Timestamp - baseTimestamp));
                        var now = HybridWaiter.Now;
                        var late = now - target;

                        // What this frame is owed since the one before it: its moment in the
                        // stream, which is what the wait between two frames has to add up to.
                        var due = Math.Max(0, target - lastTarget);
                        lastTarget = target;

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
                        else if (inPreroll)
                        {
                            // Due before the clock says so: sent as it comes, in the one burst
                            // that gives the ingest its head start.
                            waiter.WaitUntil(target, token);
                        }
                        else
                        {
                            // A live that is keeping up waits exactly what the frame is owed, and
                            // nothing here touches that. A live that fell behind waits a
                            // CatchUpRate-th of it instead, which is the same thing as sending its
                            // frames CatchUpRate times faster than real time: the shortfall is won
                            // back over the frames the jitter buffer already holds, and a run of
                            // frames that would otherwise go out as a burst goes out paced.
                            //
                            // Nothing else changes. The clock is not moved and no timestamp is
                            // rewritten, so the ingest keeps seeing one contiguous timeline,
                            // arriving slightly fast until it is even again. That is the whole
                            // difference from moving the clock, which drops the gap instead of
                            // winning it back, and a viewer reads a dropped gap as a rebuffer.
                            //
                            // A catch-up is one event, from the frame that is a second late to the
                            // first one on time again: it is counted and logged once, not once a
                            // frame. This thread is the one the frames wait on, and a line a frame
                            // into the event log is milliseconds of every frame it is behind for.
                            var behind = late > 0 ? Math.Min(late, maxLead) : 0;
                            if (behind >= lateEnough && catchingUpSince is null)
                            {
                                catchingUpSince = now;
                                Interlocked.Increment(ref _catchingUp);
                                _logger.LogWarning(
                                    "The live to {Platform} fell {Late:0.0}s behind: sending at {Rate}x to win it back",
                                    _profile.Platform,
                                    behind / (double)System.Diagnostics.Stopwatch.Frequency,
                                    CatchUpRate);
                            }
                            else if (behind == 0 && catchingUpSince is { } since)
                            {
                                catchingUpSince = null;
                                _logger.LogInformation(
                                    "The live to {Platform} is on time again after {Seconds:0.0}s",
                                    _profile.Platform,
                                    (now - since) / (double)System.Diagnostics.Stopwatch.Frequency);
                            }

                            waiter.WaitUntil(now + (behind > 0 ? due / CatchUpRate : due), token);
                        }
                    }

                    _sink.WriteTag(tag);
                    MarkSent(tag);
                    queued.Segment?.MarkSent(tag);
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
            while (_queue.TryDequeue(out var queued))
            {
                queued.Tag.Release();
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
    private QueuedTag? NextTag()
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

            var queued = _queue.Dequeue();
            _lastSentTimestamp = Math.Max(_lastSentTimestamp, queued.Tag.Timestamp);
            Monitor.PulseAll(_gate);
            return queued;
        }
    }

    /// <summary>The FLV header and the PreviousTagSize0 after it, forwarded as they were.</summary>
    private static byte[] ReadHeader(System.IO.Stream source)
    {
        var start = new byte[FlvHeaderMinimum];
        if (!ReadExactly(source, start))
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
        if (!ReadExactly(source, header.AsSpan(FlvHeaderMinimum)))
        {
            throw new InvalidDataException("The encoder ended inside the FLV header");
        }

        return header;
    }

    /// <summary>One whole tag with its trailing PreviousTagSize, or null at the end of the stream.</summary>
    private static FlvTag? ReadTag(System.IO.Stream source)
    {
        Span<byte> head = stackalloc byte[FlvTag.HeaderSize];
        if (!ReadExactly(source, head))
        {
            return null;
        }

        var dataSize = (head[1] << 16) | (head[2] << 8) | head[3];
        var timestamp = ((long)head[7] << 24) | ((long)head[4] << 16) | ((long)head[5] << 8) | head[6];
        var length = FlvTag.HeaderSize + dataSize + FlvTag.TrailerSize;

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        head.CopyTo(buffer);
        if (!ReadExactly(source, buffer.AsSpan(FlvTag.HeaderSize, dataSize + FlvTag.TrailerSize)))
        {
            // A tag cut in half is not sent: the ingest would choke on it.
            ArrayPool<byte>.Shared.Return(buffer);
            return null;
        }

        return new FlvTag((byte)(head[0] & 0x1F), timestamp, buffer, length);
    }

    private static bool ReadExactly(System.IO.Stream source, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = source.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static long Milliseconds(TimeSpan duration) => (long)duration.TotalMilliseconds;

    /// <summary>A tag waiting to be sent, with the encoder it came from.</summary>
    private readonly record struct QueuedTag(FlvTag Tag, RelaySegment? Segment);
}

/// <summary>
/// The output of one encoder inside a relay that outlives it (see
/// <see cref="FlvPacedRelay.Attach"/>): how far it was read, how far it went on air, and whether it
/// is over. A session that streams into a shared connection is measured by its segment, not by the
/// connection, which carried other encoders before it and will carry others after.
/// </summary>
public sealed class RelaySegment
{
    private readonly FlvPacedRelay _relay;
    private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _firstReadTimestamp = -1;
    private long _readPositionMilliseconds;
    private long _firstSentTimestamp = -1;
    private long _sentPositionMilliseconds;
    private long _onAirSinceTicks;
    private long _lastAdvanceTicks = Environment.TickCount64;
    private int _queued;

    internal RelaySegment(FlvPacedRelay relay, System.IO.Stream source)
    {
        _relay = relay;
        Source = source;
    }

    internal System.IO.Stream Source { get; }

    /// <summary>Set under the lock of the relay: the reader lets the segment go at the next tag.</summary>
    internal bool Detached { get; set; }

    /// <summary>The connection the segment goes out on.</summary>
    public FlvPacedRelay Relay => _relay;

    /// <summary>Completes once nothing more is read from the encoder: it ended, was let go, or the relay stopped.</summary>
    public Task ReadCompletion => _read.Task;

    /// <summary>The encoder is done with, or the connection it was going out on is gone.</summary>
    public bool Ended => _read.Task.IsCompleted || _relay.Completion.IsCompleted;

    /// <summary>
    /// How much of the encoder was taken into the relay, in milliseconds: all of it goes on air
    /// unless the connection breaks, so this is where the next encoder carries on from.
    /// </summary>
    public long ReadPositionMilliseconds => Interlocked.Read(ref _readPositionMilliseconds);

    /// <summary>How much of this encoder went on air, in milliseconds.</summary>
    public long SentPositionMilliseconds => Interlocked.Read(ref _sentPositionMilliseconds);

    /// <summary>When the first frame of this encoder went on air (Environment.TickCount64); 0 before.</summary>
    public long OnAirSinceTicks => Interlocked.Read(ref _onAirSinceTicks);

    /// <summary>When the part of this encoder on air last moved forward (Environment.TickCount64).</summary>
    public long LastAdvanceTicks => Interlocked.Read(ref _lastAdvanceTicks);

    /// <summary>Whether any frame of this encoder made it into the relay.</summary>
    public bool HasQueued => Volatile.Read(ref _queued) == 1;

    /// <summary>Stops taking frames from the encoder: what was read goes on air, the rest never does.</summary>
    public void Detach() => _relay.Detach(this);

    internal void MarkRead(long timestamp)
    {
        if (_firstReadTimestamp < 0)
        {
            _firstReadTimestamp = timestamp;
        }

        var position = timestamp - _firstReadTimestamp;
        if (position > Interlocked.Read(ref _readPositionMilliseconds))
        {
            Interlocked.Exchange(ref _readPositionMilliseconds, position);
        }
    }

    internal void MarkQueued() => Volatile.Write(ref _queued, 1);

    /// <summary>Only the sending thread of the relay calls this.</summary>
    internal void MarkSent(FlvTag tag)
    {
        if (tag.Type == FlvPacedRelay.ScriptTag)
        {
            return;
        }

        var now = Environment.TickCount64;
        Interlocked.CompareExchange(ref _onAirSinceTicks, now, 0);
        if (_firstSentTimestamp < 0)
        {
            _firstSentTimestamp = tag.Timestamp;
        }

        var position = tag.Timestamp - _firstSentTimestamp;
        if (position > Interlocked.Read(ref _sentPositionMilliseconds))
        {
            Interlocked.Exchange(ref _sentPositionMilliseconds, position);
            Interlocked.Exchange(ref _lastAdvanceTicks, now);
        }
    }

    internal void MarkReadEnded() => _read.TrySetResult();
}
