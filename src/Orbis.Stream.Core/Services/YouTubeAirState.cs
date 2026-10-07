using System.Collections.Concurrent;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// What <see cref="YouTubeAirWatch"/> knows of each live, and the rules it reads the answers of
/// YouTube by: kept apart from the watch so the rules can be read, and tested, without a live.
/// </summary>
internal sealed class YouTubeAirState
{
    /// <summary>How many answers in a row say "not on air" before the live is flagged.</summary>
    internal const int Misses = 2;

    /// <summary>How many answers in a row say "ended on YouTube" before the live is stopped.</summary>
    internal const int Endings = 2;

    private readonly ConcurrentDictionary<int, Live> _lives = new();

    /// <summary>The lives something is known about, by the video their session streams.</summary>
    public IEnumerable<int> Known => _lives.Keys;

    public bool IsOffAir(int videoPkid) => _lives.TryGetValue(videoPkid, out var live) && live.Misses >= Misses;

    /// <summary>Whether a video, or any video of the same playlist or canvas, is flagged.</summary>
    public bool IsOffAir(int videoPkid, long? historyPkid) =>
        IsOffAir(videoPkid)
        || historyPkid is { } history && _lives.Values.Any(live => live.History == history && live.Misses >= Misses);

    /// <summary>The playlist or canvas a live is part of, so its row can be told too.</summary>
    public void BelongsTo(int videoPkid, long historyPkid) => Of(videoPkid).History = historyPkid;

    /// <summary>
    /// Takes one answer about a live: found clears it and remembers the video on air, "not on air"
    /// counts one more miss, and a page that could not be read leaves it as it was. True when the
    /// flag changed.
    /// </summary>
    public bool Observe(int videoPkid, LivePlatformEmbeds.LivePlatformLookup found)
    {
        var live = Of(videoPkid);
        var before = live.Misses >= Misses;
        if (found.Embed is { } embed)
        {
            live.Misses = 0;
            live.Endings = 0;
            live.OnAirVideo = embed.VideoId ?? live.OnAirVideo;
        }
        else if (found.Reason is null)
        {
            live.Misses++;
        }

        return before != live.Misses >= Misses;
    }

    /// <summary>
    /// Takes one answer about whether the broadcast that was on air has ended. Only a yes counts;
    /// a no, or a page that could not be read or understood, starts the count again. True when the
    /// live is to be stopped.
    /// </summary>
    public bool ObserveEnding(int videoPkid, bool? ended)
    {
        var live = Of(videoPkid);
        live.Endings = ended == true ? live.Endings + 1 : 0;
        return live.Endings >= Endings;
    }

    /// <summary>The video that was last seen on air for a live, if it ever was.</summary>
    public string? OnAirVideoOf(int videoPkid) => _lives.TryGetValue(videoPkid, out var live) ? live.OnAirVideo : null;

    /// <summary>Drops what is known about a live. True when it was flagged.</summary>
    public bool Forget(int videoPkid) => _lives.TryRemove(videoPkid, out var live) && live.Misses >= Misses;

    private Live Of(int videoPkid) => _lives.GetOrAdd(videoPkid, static _ => new Live());

    /// <summary>
    /// One live. Written by the pass over the lives alone, read by the pages: a page reading a
    /// count one pass late draws the warning one pass late, nothing worse.
    /// </summary>
    private sealed class Live
    {
        public int Misses;

        public int Endings;

        public string? OnAirVideo;

        public long? History;
    }
}
