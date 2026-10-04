using System.Collections.Concurrent;
using System.Globalization;
using Orbis.Stream.Core.Configuration;

namespace Orbis.Stream.Core.Streaming;

/// <summary>One whole picture of a live, with the stamp that tells it from the one written before it.</summary>
public sealed record PreviewFrame(byte[] Bytes, string Stamp);

/// <summary>
/// Where the light preview of each live is written: one JPEG per running session, named after the
/// row the session is registered under, overwritten by ffmpeg a few times a second. It lives in the
/// data directory, next to the database, and goes away with the session.
///
/// ffmpeg writes the file where it is, shortening it before each frame, and the guarantee that a
/// reader never sees half a picture is kept here rather than with a rename on the ffmpeg side: a
/// rename over a file somebody else holds open is refused on Windows, and a preview whose frames
/// never arrive is worse than one that skips the frame it caught mid-write.
///
/// What this adds on top of reading that file is a copy of the newest picture in memory, a stamp
/// that belongs to the picture rather than to the file, and the patience to wait for the next one.
/// Together they are what the preview page needs to stop juddering, and neither of them touches the
/// live: no second transcode, no extra frame for the encoder to produce, no timer of the page's own
/// deciding when a picture is due - a page that asks for the frame it does not have is answered the
/// moment ffmpeg writes one, and is answered out of memory rather than off the disk.
/// </summary>
public sealed class LivePreviewFrames
{
    /// <summary>The suffix the versions that renamed their frames on top left behind.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>How many times a frame caught on the writing of the next one is read again.</summary>
    private const int Attempts = 4;

    /// <summary>Long enough for the write that got in the way to be over.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// How often the file is looked at while a page is waiting for a frame it does not have. Short
    /// enough that a picture is on its way the moment it is whole - a frame of a 30 fps preview is
    /// 33 milliseconds long and half of that budget is already spent on the two ends of the request -
    /// and a read of a forty kilobyte picture out of the page cache costs less than the round trip
    /// that asked for it.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(4);

    /// <summary>
    /// The most a request may be held open, whatever the page asks for. A page asks again the
    /// moment it is answered, so a shorter answer costs nothing, and a longer one would only tie up
    /// a connection of the server while nothing was being written.
    /// </summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(250);

    private const byte StartOfImage = 0xD8;

    private const byte EndOfImage = 0xD9;

    private readonly string _directory;

    /// <summary>One remembered picture per live, so the file is read once per frame and not once per page.</summary>
    private readonly ConcurrentDictionary<int, Slot> _slots = new();

    public LivePreviewFrames(OrbisRuntimeOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "preview");
    }

    /// <summary>The file ffmpeg writes the frames of a live to; the folder is made on the way.</summary>
    public string PathOf(int videoPkid)
    {
        Directory.CreateDirectory(_directory);

        // A live that starts again on the row writes its own first picture, and the last picture of
        // the live before it is not one this live wrote: the file it was written to is taken away as
        // well as the copy this holds, or the page would be answered with the frame of a live that is
        // over before ffmpeg has written anything at all.
        Discard(PendingPathOf(videoPkid));
        Discard(FramePathOf(videoPkid));

        // The counter of the stamps is left where it is, so a stamp is never handed out twice for the
        // same row: a page still holding the first stamp of the previous live would otherwise be told
        // it already has the first frame of this one.
        SlotOf(videoPkid).Forget();
        return FramePathOf(videoPkid);
    }

    /// <summary>
    /// The newest picture of a live, or null when there is none to show: the live is not running, or
    /// it has not written a whole frame yet.
    ///
    /// The picture is kept in memory, and the file is only read again to find out whether ffmpeg has
    /// written another one: two pages watching the same live, and a page asking again before the next
    /// picture is whole, are answered from the copy this holds. A frame caught half written is not
    /// taken at all, and the picture that was there is the one that stays - a page goes on with the
    /// last whole picture rather than without one.
    /// </summary>
    public PreviewFrame? Latest(int videoPkid)
    {
        var slot = SlotOf(videoPkid);

        lock (slot.Gate)
        {
            Refresh(slot, videoPkid);
            return slot.Latest;
        }
    }

    /// <summary>
    /// The next picture of a live, which is the newest one that is not the picture <paramref name="stamp"/>
    /// names, or null when ffmpeg has not written one within <paramref name="wait"/>.
    ///
    /// This is what puts the page on the same clock as ffmpeg. A page that polls on a timer of its
    /// own runs that timer against the one ffmpeg writes on, and the two drift: half the questions
    /// then land inside the same frame and come back with nothing, the other half find a frame that
    /// has been waiting, and a picture that is held for 16 milliseconds and then for 50 is what a
    /// judder is. Answered here, a frame is on its way as soon as it is written, every frame is a
    /// frame nobody else has drawn, and the page draws them on its own clock (see site.js).
    /// </summary>
    public async Task<PreviewFrame?> NextAsync(int videoPkid, string? stamp, TimeSpan wait, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);

        var slot = SlotOf(videoPkid);
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;

        while (true)
        {
            PreviewFrame? frame;
            lock (slot.Gate)
            {
                Refresh(slot, videoPkid);
                frame = slot.Latest;
            }

            if (frame is not null && !string.Equals(frame.Stamp, stamp, StringComparison.Ordinal))
            {
                return frame;
            }

            // Nothing new: either ffmpeg has not written the next picture yet or it is writing it
            // now, so there is a moment to give it rather than an answer.
            var left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(left, (long)PollInterval.TotalMilliseconds)), token).ConfigureAwait(false);
        }
    }

    /// <summary>A stopped live leaves no frame behind: the page must not show it as if it were on air.</summary>
    public void Forget(int videoPkid)
    {
        SlotOf(videoPkid).Forget();
        Discard(FramePathOf(videoPkid));
        Discard(PendingPathOf(videoPkid));
    }

    private Slot SlotOf(int videoPkid) => _slots.GetOrAdd(videoPkid, _ => new Slot());

    /// <summary>
    /// Reads the file when it holds a whole picture that is not the one in memory, and stamps it
    /// when it does. Called with the lock of the slot held, so two pages watching the same live read
    /// the file between them rather than both reading it.
    ///
    /// The stamp counts the pictures this side has seen rather than naming the write that produced
    /// them. The write time is what an earlier version used, and on Windows it is not something to
    /// trust frame by frame: NTFS keeps the timestamp of a file open for writing in its own hands,
    /// so a stamp that does not move is a page that is answered "nothing new" with a live that is
    /// happily writing thirty pictures a second - which is a preview frozen on one frame. The bytes
    /// themselves cannot lie like that: a picture that changed is a different picture.
    /// </summary>
    private void Refresh(Slot slot, int videoPkid)
    {
        if (TryRead(FramePathOf(videoPkid)) is not { } frame)
        {
            return;
        }

        if (slot.Latest is { } held && held.Bytes.AsSpan().SequenceEqual(frame))
        {
            return;
        }

        slot.Latest = new PreviewFrame(frame, (++slot.Version).ToString(CultureInfo.InvariantCulture));
    }

    private static byte[]? TryRead(string path)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(RetryDelay);
            }

            try
            {
                // Every share mode, so ffmpeg can shorten the file and write the next picture into it
                // while this reads the one before.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = new byte[stream.Length];
                stream.ReadExactly(frame);
                return IsWholePicture(frame) ? frame : null;
            }
            catch (IOException)
            {
                // The file is shorter than it was a moment ago: the write that got in the way is
                // read again instead of being answered with the front of a picture.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private string FramePathOf(int videoPkid) => Path.Combine(_directory, $"{videoPkid}.jpg");

    private string PendingPathOf(int videoPkid) => $"{FramePathOf(videoPkid)}{TemporarySuffix}";

    /// <summary>
    /// Whether the bytes are a whole picture. A JPEG begins with the start-of-image marker and ends
    /// with the end-of-image one, and that pair appears nowhere else: inside a picture an 0xFF is
    /// always followed by a 0x00 or by another marker, and the segments in front of the picture are
    /// too short to hide one. So the first end-of-image in the bytes is the end of the picture, and
    /// a frame that does not end there is one that was caught being written: no end yet, or the end
    /// of the frame before it.
    /// </summary>
    private static bool IsWholePicture(byte[] frame)
    {
        if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != StartOfImage)
        {
            return false;
        }

        var end = frame.Length - 2;
        for (var index = 2; index <= end; index++)
        {
            if (frame[index] == 0xFF && frame[index + 1] == EndOfImage)
            {
                return index == end;
            }
        }

        return false;
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still held by a reader for a moment: the next live of this row overwrites it anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What one live has already been served, and the counter that stamps its pictures.</summary>
    private sealed class Slot
    {
        public object Gate { get; } = new();

        public PreviewFrame? Latest { get; set; }

        public long Version { get; set; }

        /// <summary>
        /// Drops the picture and keeps the counter: the picture of a live that is over is not one any
        /// page should be handed, while a stamp that had already been given out must not be given out
        /// again for the row.
        /// </summary>
        public void Forget()
        {
            lock (Gate)
            {
                Latest = null;
            }
        }
    }
}
