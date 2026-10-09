using System.Diagnostics.CodeAnalysis;

namespace Orbis.Stream.Core.Services;

/// <summary>What goes on air in place of the program of a live.</summary>
public enum TakeoverKind
{
    /// <summary>A clip: it plays to its end, and the program carries on from where it was left.</summary>
    Video,

    /// <summary>A picture: it stays on air until the user resumes the live, or something else takes its place.</summary>
    Image
}

/// <summary>
/// A file on air in place of the program of a live: a spot, or the file of a scene button.
/// </summary>
/// <param name="Id">Tells two requests for the same file apart: the one that was ended is not the one asked for again.</param>
/// <param name="ButtonPkid">The scene button it came from; null for a spot.</param>
/// <param name="Cut">
/// Whether it takes the air at once, cutting whatever is in place of the program (a scene button,
/// pressed to switch now), or waits for a clip on air to end (a spot, queued after the one playing).
/// </param>
public sealed record Takeover(long Id, TakeoverKind Kind, string Path, string Label, long? ButtonPkid, bool Cut)
{
    /// <summary>Whether it stays on air until it is ended, which is what "Resume live" is shown for.</summary>
    public bool Holds => Kind == TakeoverKind.Image;
}

public sealed record ActiveSceneOverlay(
    long Id,
    long ButtonPkid,
    string Path,
    string Label,
    Orbis.Stream.Core.Domain.SceneButtonKind Kind,
    string Placement,
    int? X,
    int? Y,
    int? Width,
    int? Height,
    int? DurationSeconds,
    DateTime StartedAt);

/// <summary>What is on air in place of the program or overlaid onto it, as the pages draw it.</summary>
/// <param name="ButtonPkid">The scene button it came from; null for a spot.</param>
/// <param name="Kind"><c>VIDEO</c> or <c>IMAGE</c>.</param>
/// <param name="Holds">Whether it stays until the live is resumed or toggled: what the "Resume live" button is shown for.</param>
/// <param name="Mode">"fullscreen" or "in_scene".</param>
/// <param name="Placement">Where it sits when in_scene: "bottom-right", etc.</param>
/// <param name="DurationSeconds">How long it sits in scene; null when until stopped.</param>
public sealed record LiveScene(
    long? ButtonPkid,
    string Label,
    string Kind,
    bool Holds,
    string Mode = "fullscreen",
    string? Placement = null,
    int? DurationSeconds = null)
{
    public static LiveScene? Of(Takeover? takeover) => takeover is null
        ? null
        : new LiveScene(takeover.ButtonPkid, takeover.Label, takeover.Kind == TakeoverKind.Image ? "IMAGE" : "VIDEO", takeover.Holds, "fullscreen");

    public static LiveScene? OfOverlay(ActiveSceneOverlay? overlay) => overlay is null
        ? null
        : new(overlay.ButtonPkid,
            overlay.Label,
            overlay.Kind == Orbis.Stream.Core.Domain.SceneButtonKind.Image ? "IMAGE" : "VIDEO",
            Holds: overlay.DurationSeconds is null or <= 0,
            Mode: "in_scene",
            Placement: overlay.Placement,
            DurationSeconds: overlay.DurationSeconds);
}

/// <summary>Whether a live is running, and what is on air in place of its program right now.</summary>
public sealed record LiveSceneState(bool Live, long? History, LiveScene? Current);

/// <summary>
/// The switchboard of the running lives: for each of them, what is waiting to go on air in place
/// of the program and what is on air instead of it right now.
/// <para>The pages ask, the streaming loop does. A request is only written down here: the loop
/// that streams the live sees it at its next look (twice a second), hands the program over at the
/// position it got to, plays what was asked and brings the program back where it was left. Nothing
/// here touches a process, so a request can never land between two passes of a live and be lost,
/// which is what a flag on the ffmpeg session of the moment could.</para>
/// <para>Every live has its own queue. A spot asked for one live is played on that live, and on
/// no other: a single queue for the whole application handed it to whichever live looked first.</para>
/// <para>There is no stack: what ends goes back to the program, never to what was on air before
/// it. A picture cut by a spot does not come back after the spot; the user presses it again.</para>
/// </summary>
public sealed class LiveTakeovers
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Live> _lives = [];
    private readonly LiveChangeNotifier _notifier;
    private long _lastId;

    public LiveTakeovers(LiveChangeNotifier notifier)
    {
        _notifier = notifier;
    }

    /// <summary>
    /// The live of a history is being streamed: from here on it takes requests. The answer is what
    /// closes it again, and only it: a play that starts while the one before it is still winding
    /// down keeps its own requests when the old one is closed.
    /// </summary>
    public long Open(long history)
    {
        lock (_gate)
        {
            var live = new Live(++_lastId);
            _lives[history] = live;
            return live.Lease;
        }
    }

    /// <summary>The live is over: what was waiting for it is dropped with it.</summary>
    public void Close(long history, long lease)
    {
        var hadCurrent = false;
        lock (_gate)
        {
            if (_lives.TryGetValue(history, out var live) && live.Lease == lease)
            {
                _lives.Remove(history);
                hadCurrent = live.Current is not null || live.ActiveOverlay is not null;
            }
        }

        if (hadCurrent)
        {
            _notifier.Raise();
        }
    }

    /// <summary>Whether the live of this history is being streamed, and so whether a request for it means anything.</summary>
    public bool IsOpen(long history)
    {
        lock (_gate)
        {
            return _lives.ContainsKey(history);
        }
    }

    /// <summary>
    /// Asks for a file to go on air in place of the program. A request that cuts drops whatever
    /// was waiting: pressing a button is a switch to it now, not a place at the end of a queue.
    /// Null when the live is not running.
    /// </summary>
    public Takeover? Request(long history, TakeoverKind kind, string path, string label, long? buttonPkid, bool cut)
    {
        lock (_gate)
        {
            if (!_lives.TryGetValue(history, out var live))
            {
                return null;
            }

            var takeover = new Takeover(++_lastId, kind, path, label, buttonPkid, cut);
            if (cut)
            {
                live.Pending.Clear();
            }

            live.Pending.Add(takeover);
            return takeover;
        }
    }

    /// <summary>
    /// Back to the program: what is on air in place of it ends, and what was waiting is dropped.
    /// False when there was nothing to end.
    /// </summary>
    public bool Resume(long history)
    {
        lock (_gate)
        {
            if (!_lives.TryGetValue(history, out var live) || (live.Current is null && live.Pending.Count == 0))
            {
                return false;
            }

            live.Pending.Clear();
            if (live.Current is { } current)
            {
                live.Ended = current.Id;
            }

            return true;
        }
    }

    /// <summary>Whether something is waiting to take the place of the program: the program hands over at once.</summary>
    public bool HasPending(long history)
    {
        lock (_gate)
        {
            return _lives.TryGetValue(history, out var live) && live.Pending.Count > 0;
        }
    }

    /// <summary>The next file to go on air, which becomes the one on air. False when nothing is waiting.</summary>
    public bool TryTake(long history, [NotNullWhen(true)] out Takeover? takeover)
    {
        lock (_gate)
        {
            takeover = null;
            if (!_lives.TryGetValue(history, out var live) || live.Pending.Count == 0)
            {
                return false;
            }

            takeover = live.Pending[0];
            live.Pending.RemoveAt(0);
            live.Current = takeover;
            live.Ended = 0;
        }

        _notifier.Raise();
        return true;
    }

    /// <summary>
    /// Whether what is on air has to make way: the user resumed the live, a switch to something
    /// else was asked, or a picture - which would otherwise stay for ever - has a spot waiting.
    /// A clip on air lets a spot wait for its end.
    /// </summary>
    public bool ShouldEnd(long history, Takeover takeover)
    {
        ArgumentNullException.ThrowIfNull(takeover);
        lock (_gate)
        {
            if (!_lives.TryGetValue(history, out var live) || live.Current?.Id != takeover.Id || live.Ended == takeover.Id)
            {
                return true;
            }

            return live.Pending.Any(next => next.Cut) || (takeover.Holds && live.Pending.Count > 0);
        }
    }

    /// <summary>The program is back on air: nothing is in its place any more.</summary>
    public void BackToProgram(long history)
    {
        bool changed;
        lock (_gate)
        {
            changed = _lives.TryGetValue(history, out var live) && live.Current is not null;
            if (changed)
            {
                live!.Current = null;
                live.Ended = 0;
            }
        }

        if (changed)
        {
            _notifier.Raise();
        }
    }

    /// <summary>What is on air in place of the program of the live; null while the program is.</summary>
    public Takeover? CurrentOf(long? history)
    {
        if (history is not { } pkid)
        {
            return null;
        }

        lock (_gate)
        {
            return _lives.TryGetValue(pkid, out var live) ? live.Current : null;
        }
    }

    /// <summary>
    /// Returns the live scene currently active (takeover or overlay) on the live; null if none.
    /// </summary>
    public LiveScene? LiveSceneOf(long? history)
    {
        var current = CurrentOf(history);
        if (current is not null)
        {
            return LiveScene.Of(current);
        }

        var overlay = ActiveOverlayOf(history);
        return LiveScene.OfOverlay(overlay);
    }

    /// <summary>
    /// Sets an in-scene overlay (banner, GIF, video) over the live composition at the given placement.
    /// </summary>
    public ActiveSceneOverlay? RequestOverlay(
        long history,
        long buttonPkid,
        string path,
        string label,
        Orbis.Stream.Core.Domain.SceneButtonKind kind,
        string placement,
        int? x,
        int? y,
        int? width,
        int? height,
        int? durationSeconds)
    {
        lock (_gate)
        {
            if (!_lives.TryGetValue(history, out var live))
            {
                return null;
            }

            var overlay = new ActiveSceneOverlay(
                ++_lastId, buttonPkid, path, label, kind, placement, x, y, width, height, durationSeconds, DateTime.Now);
            live.ActiveOverlay = overlay;
            return overlay;
        }
    }

    /// <summary>
    /// Removes the in-scene overlay from the live composition.
    /// </summary>
    public bool StopOverlay(long history, long? buttonPkid = null)
    {
        bool changed;
        lock (_gate)
        {
            if (!_lives.TryGetValue(history, out var live) || live.ActiveOverlay is null)
            {
                return false;
            }

            if (buttonPkid.HasValue && live.ActiveOverlay.ButtonPkid != buttonPkid.Value)
            {
                return false;
            }

            live.ActiveOverlay = null;
            changed = true;
        }

        if (changed)
        {
            _notifier.Raise();
        }

        return changed;
    }

    /// <summary>
    /// The active in-scene overlay on the live; null if none or if its duration has expired.
    /// </summary>
    public ActiveSceneOverlay? ActiveOverlayOf(long? history)
    {
        if (history is not { } pkid)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_lives.TryGetValue(pkid, out var live) || live.ActiveOverlay is null)
            {
                return null;
            }

            if (live.ActiveOverlay.DurationSeconds is > 0 and var duration)
            {
                if (DateTime.Now >= live.ActiveOverlay.StartedAt.AddSeconds(duration))
                {
                    live.ActiveOverlay = null;
                    return null;
                }
            }

            return live.ActiveOverlay;
        }
    }

    /// <summary>Whether a file is on air, or waiting to be, on any live: it is not deleted from under it.</summary>
    public bool Uses(string path)
    {
        lock (_gate)
        {
            return _lives.Values.Any(live =>
                string.Equals(live.Current?.Path, path, StringComparison.Ordinal)
                || string.Equals(live.ActiveOverlay?.Path, path, StringComparison.Ordinal)
                || live.Pending.Any(next => string.Equals(next.Path, path, StringComparison.Ordinal)));
        }
    }

    private sealed class Live(long lease)
    {
        public long Lease { get; } = lease;

        public List<Takeover> Pending { get; } = [];

        public Takeover? Current { get; set; }

        /// <summary>The id of the takeover on air that was asked to end; zero when none was.</summary>
        public long Ended { get; set; }

        public ActiveSceneOverlay? ActiveOverlay { get; set; }
    }
}
