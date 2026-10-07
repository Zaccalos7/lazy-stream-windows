using System.Collections.Concurrent;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// What <see cref="YouTubeAirWatch"/> knows of each live, and the rules it reads the answers of
/// YouTube by: kept apart from the watch so the rules can be read, and tested, without a live.
/// <para>A live is its history - the playlist or the canvas - and not one of its ffmpeg: every
/// video, spot or restarted pass is a session of its own on the same connection, and a warning
/// tied to a session went away and started its wait again each time the live moved on. Tied to
/// the history it stays up until the live is found on air, the live is over, or the user closes
/// it.</para>
/// </summary>
internal sealed class YouTubeAirState
{
    /// <summary>How many answers in a row say "not on air" before the live is flagged.</summary>
    internal const int Misses = 2;

    /// <summary>How many answers in a row say "ended on YouTube" before the live is stopped.</summary>
    internal const int Endings = 2;

    /// <summary>
    /// How many passes in a row a live may have no session before it is forgotten: between two
    /// videos of a playlist there is a moment with nothing running, and that is not the end.
    /// </summary>
    internal const int Absences = 2;

    private readonly ConcurrentDictionary<long, Live> _lives = new();

    /// <summary>The lives something is known about, by their history.</summary>
    public IEnumerable<long> Known => _lives.Keys;

    /// <summary>Whether the live is sent to YouTube and not shown there, and the user has not closed the warning.</summary>
    public bool IsOffAir(long? history) => Find(history) is { Dismissed: false } live && live.Misses >= Misses;

    /// <summary>
    /// Whether YouTube could not be asked about a live, answer after answer: the channel of the
    /// configuration is not a page YouTube has (a name instead of the handle), or YouTube does not
    /// answer this machine. Nothing is known about the live then, and that is said too.
    /// </summary>
    public bool IsUnverified(long? history) => Find(history) is { Dismissed: false } live && live.Failures >= Misses;

    /// <summary>
    /// Since when the live has been on air, from the first time it was seen so: the wait before
    /// YouTube is asked runs once for the whole live, not again for every video of it.
    /// </summary>
    public DateTimeOffset OnAirSince(long history, DateTimeOffset now)
    {
        var live = Of(history);
        live.Absent = 0;
        return live.Since ??= now;
    }

    /// <summary>
    /// One pass that found no session of the live running. True when the live is to be forgotten,
    /// and with it any warning it had up.
    /// </summary>
    public bool Missing(long history)
    {
        if (!_lives.TryGetValue(history, out var live))
        {
            return false;
        }

        live.Absent++;
        return live.Absent >= Absences && _lives.TryRemove(history, out _);
    }

    /// <summary>
    /// Takes one answer about a live: found clears it and remembers the video on air, "not on air"
    /// counts one more miss, and a page that could not be read leaves the miss count as it was and
    /// counts a failure instead. True when what the pages show changed.
    /// </summary>
    public bool Observe(long history, LivePlatformEmbeds.LivePlatformLookup found)
    {
        var live = Of(history);
        var before = Shown(live);
        if (found.Embed is { } embed)
        {
            live.Misses = 0;
            live.Endings = 0;
            live.Failures = 0;
            // Resolved: a warning that comes back later is a new one, and is shown again.
            live.Dismissed = false;
            live.OnAirVideo = embed.VideoId ?? live.OnAirVideo;
        }
        else if (found.Reason is null)
        {
            live.Misses++;
            live.Failures = 0;
        }
        else
        {
            live.Failures++;
        }

        return before != Shown(live);
    }

    /// <summary>The user closed the warning: it stays closed until the live is found on air.</summary>
    public bool Dismiss(long history)
    {
        if (!_lives.TryGetValue(history, out var live))
        {
            return false;
        }

        var before = Shown(live);
        live.Dismissed = true;
        return before != Shown(live);
    }

    /// <summary>
    /// Takes one answer about whether the broadcast that was on air has ended. Only a yes counts;
    /// a no, or a page that could not be read or understood, starts the count again. True when the
    /// live is to be stopped.
    /// </summary>
    public bool ObserveEnding(long history, bool? ended)
    {
        var live = Of(history);
        live.Endings = ended == true ? live.Endings + 1 : 0;
        return live.Endings >= Endings;
    }

    /// <summary>The video that was last seen on air for a live, if it ever was.</summary>
    public string? OnAirVideoOf(long history) => Find(history)?.OnAirVideo;

    /// <summary>Drops what is known about a live. True when it had a warning showing.</summary>
    public bool Forget(long history) => _lives.TryRemove(history, out var live) && Shown(live) != (false, false);

    private static (bool OffAir, bool Unverified) Shown(Live live) =>
        (!live.Dismissed && live.Misses >= Misses, !live.Dismissed && live.Failures >= Misses);

    private Live? Find(long? history) => history is { } key && _lives.TryGetValue(key, out var live) ? live : null;

    private Live Of(long history) => _lives.GetOrAdd(history, static _ => new Live());

    /// <summary>
    /// One live. Written by the pass over the lives and by a dismissal, read by the pages: a page
    /// reading a count one pass late draws the warning one pass late, nothing worse.
    /// </summary>
    private sealed class Live
    {
        public int Misses;

        public int Endings;

        public int Failures;

        public int Absent;

        public bool Dismissed;

        public DateTimeOffset? Since;

        public string? OnAirVideo;
    }
}
