namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The pace at which a platform adapts the bitrate of a live (see <see cref="BitrateLadder"/>). It is
/// the part of the adaptation that differs between the two deliveries, so it is the part each
/// profile names for itself; what the ladder does with it is the same for every platform.
/// </summary>
/// <param name="Window">
/// How long the encoder has to stay ahead of a network that does not take its frames for the
/// network to be the one holding the live back.
/// </param>
/// <param name="MaxDrift">How far behind real time the live may slip before its bitrate comes down.</param>
/// <param name="ProbeAfter">How long the live has to go out clean before a step back up is tried.</param>
public sealed record RateAdaptation(TimeSpan Window, TimeSpan MaxDrift, TimeSpan ProbeAfter)
{
    /// <summary>
    /// Twitch and Kick: a player a couple of seconds behind the live, whose buffer is gone after
    /// one bad stretch of the network. The ladder answers within seconds, and lets the live slip no
    /// further than twice the jitter buffer of the relay.
    /// </summary>
    public static readonly RateAdaptation LowLatency = new(
        Window: TimeSpan.FromSeconds(6),
        MaxDrift: TimeSpan.FromSeconds(2),
        ProbeAfter: TimeSpan.FromMinutes(2));

    /// <summary>
    /// YouTube and Facebook Gaming: a head start and a player that buffers far more, so a slow
    /// stretch is ridden out before it costs a switch - each one is an encoder started again and a
    /// new sequence header the platform has to take in.
    /// </summary>
    public static readonly RateAdaptation Buffered = new(
        Window: TimeSpan.FromSeconds(10),
        MaxDrift: TimeSpan.FromSeconds(6),
        ProbeAfter: TimeSpan.FromMinutes(3));
}

/// <summary>
/// What the relay had put on air at one moment: the clock it was read on
/// (<c>Environment.TickCount64</c>), the media time and the bytes handed to the ingest, and how far
/// the encoder was ahead of them. The first three are counters, so two readings make a rate.
/// </summary>
public readonly record struct DeliverySample(long Ticks, long MediaMilliseconds, long Bytes, long LeadMilliseconds);

/// <summary>A change of the video bitrate of a live: from what, to what, and what the network was measured to carry.</summary>
/// <param name="Throughput">Bits per second the ingest took while frames were waiting for it.</param>
public sealed record RateDecision(int From, int To, long Throughput)
{
    public bool Lowers => To < From;
}

/// <summary>
/// The video bitrate a live goes out at, following what the network carries: the server side of
/// adaptive streaming, the only side a publisher has. The platform makes the renditions; what the
/// publisher chooses is the rate of the one stream it sends, and that rate has to stay under what
/// the network takes, or the ingest receives the live slower than it plays.
/// <para>That is what the relay measures (see <see cref="DeliverySample"/>): the media time that
/// went on air against the wall clock, ρ. One when the network carries the live; under one when it
/// does not, and then the live needs <c>1/ρ</c> seconds to send every second of it. It is the
/// download time over the segment time of an adaptive client, <c>T_D / T_S = R_C / S</c>: past one
/// the buffer of the viewers drains, and the stall that follows is what a live is punished for
/// the most - far more than for the step down in quality that would have prevented it.</para>
/// <para>What triggers the step is the drift: how far the media on air has fallen behind the
/// best pace it kept, which is ρ added up over time. A dip of the network leaves a little of it,
/// and the relay wins it back by sending faster than real time; a network that carries less than
/// the live leaves more of it every second. So the bitrate comes down once the live has slipped
/// <see cref="RateAdaptation.MaxDrift"/> and is still not winning it back - not on a dip, and not
/// on a network that stopped altogether, which is a stall and is the stall detection's to call.</para>
/// <para>A live can fall behind for two reasons, and only one of them is the network. An encoder
/// that does not keep up starves the relay (its queue runs empty, see
/// <see cref="FlvPacedRelay.LowLeadSinceTicks"/>), and a lower bitrate would not make it faster:
/// that one is answered with a lighter preset. So the bitrate only comes down while frames are
/// waiting for the network - the jitter buffer at least half full across the whole window.</para>
/// <para>The throughput is measured the way a chunked delivery has to be measured: only over a
/// time in which the network was holding frames back. A paced relay is idle between frames by
/// design, and bytes over elapsed time counts that idle time as a slow network - the error that
/// makes an adaptive client under chunked transfer think the network slower than it is. Here it
/// would do the opposite: a window that is half on time measures the rate the relay sent at, not
/// what the network could take. So the throughput is read over the last third of the window
/// only, the part in which the live is still behind.</para>
/// <para>Every switch is an encoder started again, and a quality that keeps moving is worse than
/// one that is a little low: the bitrate goes down at once to what the network carried, with
/// headroom to win the drift back (never more than half of it in one step), and climbs back only
/// in steps, after minutes of a clean live, with the wait doubled every time a step up has to be
/// taken back.</para>
/// </summary>
public sealed class BitrateLadder
{
    /// <summary>The share of the measured throughput the live goes on at: the rest wins the drift back.</summary>
    private const double Headroom = 0.85;

    /// <summary>
    /// ρ under which the network is not slow but down: a live that hardly moves is a stall, and a
    /// throughput measured on it is no measure of what the network carries once it is back.
    /// </summary>
    private const double MinimumProgress = 0.1;

    /// <summary>ρ a window has to keep for the live to count as clean, and a step up to be considered.</summary>
    private const double CleanRate = 0.97;

    /// <summary>How full the jitter buffer has to be, on average, for the network to be what holds the live back.</summary>
    private const double Backlogged = 0.5;

    /// <summary>The smallest step down: under fifteen percent a restart costs more than it saves.</summary>
    private const double SmallestStep = 0.85;

    /// <summary>
    /// The largest step down: half. What the network carried in the last seconds can be a bad
    /// moment rather than what it carries, and one switch is never allowed to take the live to the
    /// floor on it; a network that is that bad goes on falling behind, and the next step follows.
    /// </summary>
    private const double LargestStep = 0.5;

    /// <summary>A step back up, as a share of the bitrate of the setting.</summary>
    private const double StepUp = 0.2;

    /// <summary>
    /// The lowest share of the setting the ladder goes down to. Under it a live is not worth
    /// watching anyway, and a network that cannot carry even that is the stall detection's to call.
    /// </summary>
    private const double FloorShare = 0.3;

    /// <summary>The lowest bitrate the ladder goes down to, whatever the setting.</summary>
    private const int FloorBitrate = 500_000;

    /// <summary>The bitrates of the ladder are whole tens of kilobits: a log of them reads as numbers a person chose.</summary>
    private const int Granularity = 10_000;

    /// <summary>A drift under this is a live on time.</summary>
    private const long OnTimeMilliseconds = 500;

    /// <summary>The closest two readings are kept to each other: the window is measured, not oversampled.</summary>
    private const long SampleSpacingMilliseconds = 200;

    /// <summary>Readings kept: at one every <see cref="SampleSpacingMilliseconds"/> at the most, more than the longest window.</summary>
    private const int Capacity = 128;

    /// <summary>
    /// How long a new connection, or a pass that has just switched, is left to fill its buffer and
    /// send its head start before it is judged.
    /// </summary>
    private static readonly long SettleMilliseconds = (long)TimeSpan.FromSeconds(5).TotalMilliseconds;

    /// <summary>The longest wait between two steps up, however many of them failed.</summary>
    private static readonly long LongestProbeMilliseconds = (long)TimeSpan.FromMinutes(16).TotalMilliseconds;

    private readonly RateAdaptation? _rules;
    private readonly long _backlog;
    private readonly long _window;
    private readonly long _recent;
    private readonly long _maxDrift;
    private readonly long _baseProbe;
    private readonly DeliverySample[] _samples = new DeliverySample[Capacity];
    private readonly object _gate = new();

    private int _count;
    private int _newest = -1;
    private bool _started;
    private long _settleUntil;
    private long _lastChange;
    private long _cleanSince;
    private long _probeAfter;
    private bool _probing;
    private DeliverySample _origin;
    private long _bestAhead;
    private int? _cap;

    /// <param name="rules">The pace of the platform; null is a ladder that never moves.</param>
    /// <param name="maxLead">The jitter buffer of the relay, which "frames are waiting for the network" is measured against.</param>
    public BitrateLadder(RateAdaptation? rules, TimeSpan maxLead)
    {
        _rules = rules;
        _backlog = (long)(maxLead.TotalMilliseconds * Backlogged);
        if (rules is null)
        {
            return;
        }

        _window = (long)rules.Window.TotalMilliseconds;
        _recent = _window / 3;
        _maxDrift = (long)rules.MaxDrift.TotalMilliseconds;
        _baseProbe = (long)rules.ProbeAfter.TotalMilliseconds;
        _probeAfter = _baseProbe;
    }

    /// <summary>
    /// The video bitrate the live goes out at while the network asks for less than the setting;
    /// null while the live goes out at the bitrate of the setting.
    /// </summary>
    public int? Cap
    {
        get
        {
            lock (_gate)
            {
                return _cap;
            }
        }
    }

    /// <summary>The video bitrate a pass goes out at: the one of the setting, or the cap under it.</summary>
    public int? BitrateFor(int? nominal) =>
        nominal is > 0 && Cap is { } cap && cap < nominal ? cap : nominal;

    /// <summary>
    /// Takes one reading of the relay and says whether the bitrate of the live has to change: null
    /// while it holds. A change is already in force when it is returned, for every pass that starts
    /// after it; starting one is the caller's to do.
    /// </summary>
    /// <param name="nominal">The video bitrate of the setting: what the ladder climbs back to.</param>
    /// <param name="audioBitrate">The bitrate of the sound, which the throughput carries next to the picture.</param>
    public RateDecision? Observe(DeliverySample sample, int nominal, int audioBitrate)
    {
        if (_rules is null || nominal <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            var now = sample.Ticks;
            if (!_started)
            {
                _started = true;
                _lastChange = now;
                Settle(now);
            }

            // A setting brought under the cap while the live runs: the setting is the lower of the two.
            if (_cap >= nominal)
            {
                _cap = null;
            }

            if (now < _settleUntil)
            {
                return null;
            }

            if (_count > 0)
            {
                var last = _samples[_newest];

                // Counters that went back are a new connection: it starts from nothing, and settles
                // before it is judged. The cap stays - the network that broke the last connection is
                // the one this one goes out on.
                if (sample.MediaMilliseconds < last.MediaMilliseconds || sample.Bytes < last.Bytes)
                {
                    Settle(now);
                    return null;
                }

                if (now - last.Ticks < SampleSpacingMilliseconds)
                {
                    return null;
                }
            }

            Record(sample);

            // How far the media on air is ahead of the wall clock since the live settled, and how
            // far it has fallen from the best it was: the drift, which a live on time never has.
            var ahead = sample.MediaMilliseconds - _origin.MediaMilliseconds - (now - _origin.Ticks);
            _bestAhead = Math.Max(_bestAhead, ahead);
            var drift = _bestAhead - ahead;

            if (!TryMeasure(sample, out var window))
            {
                return null;
            }

            var backlogged = window.AverageLead >= _backlog;
            var stillBehind = window.RecentRate is >= MinimumProgress and <= 1d;
            if (backlogged && stillBehind && drift >= _maxDrift)
            {
                // Not a clean moment, whatever the ladder can still do about it: a live held at the
                // floor by a network that cannot carry even that is not one to step up from.
                _cleanSince = now;
                return Lower(now, nominal, _cap ?? nominal, audioBitrate, window.RecentThroughput);
            }

            if (drift >= OnTimeMilliseconds || window.Rate < CleanRate)
            {
                _cleanSince = now;
            }

            return _cap is { } cap && now - _cleanSince >= _probeAfter && now - _lastChange >= _probeAfter
                ? Raise(now, nominal, cap, window.RecentThroughput)
                : null;
        }
    }

    /// <summary>Called under the lock.</summary>
    private RateDecision? Lower(long now, int nominal, int current, int audioBitrate, long throughput)
    {
        var floor = Math.Min(nominal, Math.Max(FloorBitrate, (int)(nominal * FloorShare)));
        var carried = (long)(throughput * Headroom) - audioBitrate;
        var target = (int)Math.Clamp(carried, current * LargestStep, current * SmallestStep);
        target = Math.Max(floor, target - target % Granularity);
        if (target >= current)
        {
            // At the floor already: there is nothing left to give.
            return null;
        }

        if (_probing)
        {
            // A step up that is taken back before it had its time was a step too far: the next one
            // waits twice as long. One that held is a network that changed since, and the wait
            // starts over.
            _probeAfter = now - _lastChange < _probeAfter
                ? Math.Min(_probeAfter * 2, LongestProbeMilliseconds)
                : _baseProbe;
            _probing = false;
        }

        _cap = target;
        _lastChange = now;
        Settle(now);
        return new RateDecision(current, target, throughput);
    }

    /// <summary>Called under the lock.</summary>
    private RateDecision Raise(long now, int nominal, int cap, long throughput)
    {
        if (_probing)
        {
            // The step before this one held: the network takes it, and the wait is the platform's again.
            _probeAfter = _baseProbe;
        }

        var target = cap + (int)(nominal * StepUp);
        target -= target % Granularity;
        _cap = target < nominal ? target : null;
        _probing = true;
        _lastChange = now;
        Settle(now);
        return new RateDecision(cap, _cap ?? nominal, throughput);
    }

    /// <summary>Called under the lock: the readings start again, after the live had its time to settle.</summary>
    private void Settle(long now)
    {
        _count = 0;
        _newest = -1;
        _settleUntil = now + SettleMilliseconds;
        _cleanSince = now;
    }

    /// <summary>Called under the lock.</summary>
    private void Record(DeliverySample sample)
    {
        if (_count == 0)
        {
            _origin = sample;
            _bestAhead = 0;
        }

        _newest = (_newest + 1) % Capacity;
        _samples[_newest] = sample;
        _count = Math.Min(_count + 1, Capacity);
    }

    /// <summary>
    /// The rates over the window that ends on <paramref name="newest"/> and over its last third;
    /// false until the readings span most of the window, which is what keeps one late reading of
    /// the monitor from being read as the network.
    /// </summary>
    private bool TryMeasure(DeliverySample newest, out Window window)
    {
        var now = newest.Ticks;
        var oldest = newest;
        var recent = newest;
        long leadSum = 0;
        var readings = 0;
        for (var index = 0; index < _count; index++)
        {
            var sample = _samples[(_newest - index + Capacity) % Capacity];
            var age = now - sample.Ticks;
            if (age > _window)
            {
                break;
            }

            oldest = sample;
            if (age <= _recent)
            {
                recent = sample;
            }

            leadSum += sample.LeadMilliseconds;
            readings++;
        }

        var span = now - oldest.Ticks;
        var recentSpan = now - recent.Ticks;
        if (span < _window * 4 / 5 || recentSpan <= 0)
        {
            window = default;
            return false;
        }

        window = new Window(
            Rate: (double)(newest.MediaMilliseconds - oldest.MediaMilliseconds) / span,
            RecentRate: (double)(newest.MediaMilliseconds - recent.MediaMilliseconds) / recentSpan,
            RecentThroughput: (newest.Bytes - recent.Bytes) * 8_000 / recentSpan,
            AverageLead: leadSum / readings);
        return true;
    }

    /// <summary>
    /// What a window of readings says: ρ over all of it and over its last third, the bits per
    /// second of that last third, and the average lead of the encoder.
    /// </summary>
    private readonly record struct Window(double Rate, double RecentRate, long RecentThroughput, long AverageLead);
}
