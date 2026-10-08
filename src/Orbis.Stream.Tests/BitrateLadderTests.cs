using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// The bitrate of a live follows the network: down once the network leaves the live behind and
/// does not let it catch up, back up in steps once it has carried it cleanly for a while, and
/// never because of an encoder that is the slow one, a dip the relay wins back, or a network that
/// stopped altogether.
/// </summary>
public sealed class BitrateLadderTests
{
    private const int Nominal = 6_000_000;
    private const int Audio = 160_000;

    /// <summary>What the stream weighs on the wire at the bitrate of the setting: picture and sound.</summary>
    private const long OnTheWire = Nominal + Audio;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Twitch: a six second window, two seconds of drift, two minutes before a step up.</summary>
    private static BitrateLadder Ladder() => new(RateAdaptation.LowLatency, TimeSpan.FromSeconds(1));

    [Fact]
    public void ALiveTheNetworkCarriesKeepsTheBitrateOfTheSetting()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);

        Assert.Null(relay.Run(TimeSpan.FromMinutes(5), rate: 1, OnTheWire));
        Assert.Null(ladder.Cap);
        Assert.Equal(Nominal, ladder.BitrateFor(Nominal));
    }

    [Fact]
    public void ANetworkThatCarriesLessThanTheLiveBringsTheBitrateDownToWhatItCarries()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));

        // An uplink of 4 Mbps under a live of 6.16.
        var decision = relay.Run(TimeSpan.FromSeconds(30), rate: 0.65, 4_000_000);

        Assert.NotNull(decision);
        Assert.True(decision.Lowers);
        Assert.Equal(Nominal, decision.From);
        Assert.Equal(4_000_000, decision.Throughput);
        // 85% of what got through, less the sound: room for the relay to win the drift back.
        Assert.Equal(3_240_000, decision.To);
        Assert.Equal(3_240_000, ladder.Cap);
        Assert.Equal(3_240_000, ladder.BitrateFor(Nominal));
        Assert.Null(ladder.BitrateFor(null));

        // Two seconds behind at 0.65 of real time is a little under six seconds of it.
        Assert.InRange(relay.Elapsed.TotalSeconds, 5.5, 6.5);
    }

    [Fact]
    public void AnEncoderThatDoesNotKeepUpIsNotTheNetworksFault()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);

        // The live falls behind with nothing queued: the encoder is late, not the network, and a
        // lower bitrate would not make it any faster (that one is answered with a lighter preset).
        Assert.Null(relay.Run(Minute, rate: 0.65, 4_000_000, lead: 0));
        Assert.Null(ladder.Cap);
    }

    [Fact]
    public void ADipTheRelayWinsBackIsNoReasonToSwitch()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));

        // Two bad seconds leave 1.6 of drift, and the relay wins it back sending faster than real time.
        Assert.Null(relay.Run(TimeSpan.FromSeconds(2), rate: 0.2, 1_232_000));
        Assert.Null(relay.Run(TimeSpan.FromSeconds(3), rate: 1.6, OnTheWire * 16 / 10));
        Assert.Null(relay.Run(Minute, rate: 1, OnTheWire));
        Assert.Null(ladder.Cap);
    }

    [Fact]
    public void ANetworkThatStoppedIsAStallNotABitrate()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));

        // Nothing goes through at all: what it carries is no measure of anything, and the stall
        // detection of the live is what decides.
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 0, 0));
        Assert.Null(ladder.Cap);
    }

    [Fact]
    public void ASlowLeakIsCaughtOnceTheLiveHasSlippedTheMaxDrift()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));

        // Five percent short: never a bad moment, and two seconds behind after forty.
        var decision = relay.Run(Minute, rate: 0.95, OnTheWire * 95 / 100);

        Assert.NotNull(decision);
        Assert.Equal(4_810_000, decision.To);
        Assert.InRange(relay.Elapsed.TotalSeconds, 39, 41);
    }

    [Fact]
    public void OneStepNeverMoreThanHalvesTheBitrateAndTheFloorHoldsIt()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));

        // A network at a fifth of the live: half at once, half again, then the floor (30%).
        Assert.Equal(3_000_000, relay.Run(Minute, rate: 0.2, 1_232_000)?.To);
        Assert.Equal(1_800_000, relay.Run(Minute, rate: 0.4, 1_232_000)?.To);
        Assert.Null(relay.Run(Minute, rate: 0.6, 1_232_000));
        Assert.Equal(1_800_000, ladder.Cap);
    }

    [Fact]
    public void TheLiveClimbsBackInStepsOnceItWentOutClean()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));
        Assert.Equal(3_240_000, relay.Run(Minute, rate: 0.65, 4_000_000)?.To);

        // Two clean minutes for every step of a fifth of the setting, until the setting itself.
        var first = relay.Run(TimeSpan.FromMinutes(3), rate: 1, 3_400_000);
        Assert.Equal(new RateDecision(3_240_000, 4_440_000, 3_400_000), first);
        Assert.InRange(relay.Elapsed.TotalSeconds, 119, 121);

        Assert.Equal(5_640_000, relay.Run(TimeSpan.FromMinutes(3), rate: 1, 5_800_000)?.To);
        Assert.Equal(Nominal, relay.Run(TimeSpan.FromMinutes(3), rate: 1, OnTheWire)?.To);
        Assert.Null(ladder.Cap);
        Assert.Null(relay.Run(TimeSpan.FromMinutes(10), rate: 1, OnTheWire));
    }

    [Fact]
    public void AStepUpTakenBackDoublesTheWaitBeforeTheNext()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));
        Assert.Equal(3_240_000, relay.Run(Minute, rate: 0.65, 4_000_000)?.To);
        Assert.Equal(4_440_000, relay.Run(TimeSpan.FromMinutes(3), rate: 1, 3_400_000)?.To);

        // The network does not take the step: it is taken back...
        Assert.True(relay.Run(Minute, rate: 0.8, 4_000_000)?.Lowers);

        // ...and the next one waits four minutes instead of two.
        Assert.Null(relay.Run(TimeSpan.FromMinutes(3), rate: 1, 3_400_000));
        Assert.NotNull(relay.Run(TimeSpan.FromMinutes(2), rate: 1, 3_400_000));
    }

    [Fact]
    public void ANewConnectionKeepsTheBitrateTheNetworkLeftTheLiveAt()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));
        Assert.Equal(3_240_000, relay.Run(Minute, rate: 0.65, 4_000_000)?.To);

        // A reconnection: a relay whose counters start again from nothing.
        relay.Reconnect();
        Assert.Null(relay.Run(TimeSpan.FromSeconds(30), rate: 1, 3_400_000));
        Assert.Equal(3_240_000, ladder.Cap);
    }

    [Fact]
    public void ASettingBroughtUnderTheCapIsTheBitrateOfTheLive()
    {
        var ladder = Ladder();
        var relay = new Relay(ladder);
        Assert.Null(relay.Run(TimeSpan.FromSeconds(10), rate: 1, OnTheWire));
        Assert.Equal(3_240_000, relay.Run(Minute, rate: 0.65, 4_000_000)?.To);

        relay.Nominal = 3_000_000;
        Assert.Null(relay.Run(TimeSpan.FromSeconds(1), rate: 1, 3_160_000));

        Assert.Null(ladder.Cap);
        Assert.Equal(3_000_000, ladder.BitrateFor(3_000_000));
    }

    [Fact]
    public void ALadderWithoutRulesOrASettingWithoutBitrateNeverMoves()
    {
        var still = new BitrateLadder(null, TimeSpan.FromSeconds(1));
        Assert.Null(new Relay(still).Run(Minute, rate: 0.3, 1_800_000));
        Assert.Null(still.Cap);

        var ladder = Ladder();
        Assert.Null(new Relay(ladder) { Nominal = 0 }.Run(Minute, rate: 0.3, 1_800_000));
        Assert.Null(ladder.Cap);
    }

    [Fact]
    public void YouTubeRidesOutWhatTwitchAnswers()
    {
        // The same five seconds at 0.65 of real time: past the two seconds Twitch allows, inside
        // the six YouTube (and its player) can take.
        var twitch = new Relay(Ladder());
        var youtube = new Relay(new BitrateLadder(RateAdaptation.Buffered, TimeSpan.FromSeconds(4)));
        foreach (var relay in new[] { twitch, youtube })
        {
            Assert.Null(relay.Run(TimeSpan.FromSeconds(15), rate: 1, OnTheWire, lead: 4000));
        }

        Assert.NotNull(twitch.Run(TimeSpan.FromSeconds(8), rate: 0.65, 4_000_000, lead: 4000));
        Assert.Null(youtube.Run(TimeSpan.FromSeconds(8), rate: 0.65, 4_000_000, lead: 4000));
    }

    /// <summary>
    /// A relay as the monitor reads it, twice a second: the network takes <c>rate</c> of real time
    /// at <c>bitsPerSecond</c>, with <c>lead</c> milliseconds of the encoder queued in the relay.
    /// </summary>
    private sealed class Relay(BitrateLadder ladder)
    {
        private const long Poll = 500;

        private long _now = 1_000_000;
        private long _media;
        private long _bytes;

        public int Nominal { get; set; } = BitrateLadderTests.Nominal;

        /// <summary>How long the last run went on before the ladder decided, or all of it.</summary>
        public TimeSpan Elapsed { get; private set; }

        /// <summary>Runs until the ladder decides something, which is when a pass would start again.</summary>
        public RateDecision? Run(TimeSpan duration, double rate, long bitsPerSecond, long lead = 1000)
        {
            for (var elapsed = Poll; elapsed <= duration.TotalMilliseconds; elapsed += Poll)
            {
                _now += Poll;
                _media += (long)(Poll * rate);
                _bytes += bitsPerSecond * Poll / 8000;
                Elapsed = TimeSpan.FromMilliseconds(elapsed);
                if (ladder.Observe(new DeliverySample(_now, _media, _bytes, lead), Nominal, Audio) is { } decision)
                {
                    return decision;
                }
            }

            return null;
        }

        public void Reconnect()
        {
            _media = 0;
            _bytes = 0;
        }
    }
}
