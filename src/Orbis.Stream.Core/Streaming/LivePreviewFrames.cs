using Orbis.Stream.Core.Configuration;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// Where the light preview of each live is written: one JPEG per running session, named after the
/// row the session is registered under, overwritten by ffmpeg a few times a second. It lives in the
/// data directory, next to the database, and goes away with the session.
///
/// ffmpeg writes the file where it is, shortening it before each frame, and the guarantee that a
/// reader never sees half a picture is kept here rather than with a rename on the ffmpeg side: a
/// rename over a file somebody else holds open is refused on Windows, and a preview whose frames
/// never arrive is worse than one that skips the frame it caught mid-write.
/// </summary>
public sealed class LivePreviewFrames
{
    /// <summary>The suffix the versions that renamed their frames on top left behind.</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>How many times a frame caught on the writing of the next one is read again.</summary>
    private const int Attempts = 4;

    /// <summary>Long enough for the write that got in the way to be over.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1);

    private const byte StartOfImage = 0xD8;

    private const byte EndOfImage = 0xD9;

    private readonly string _directory;

    public LivePreviewFrames(OrbisRuntimeOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "preview");
    }

    /// <summary>The file ffmpeg writes the frames of a live to; the folder is made on the way.</summary>
    public string PathOf(int videoPkid)
    {
        Directory.CreateDirectory(_directory);
        Discard(PendingPathOf(videoPkid));
        return FramePathOf(videoPkid);
    }

    /// <summary>
    /// The last frame of a live, or null when there is none to show: the live is not running, it has
    /// not written a frame yet, or the frame it was writing is not finished. Read with every share
    /// mode, so ffmpeg can write over the file while it is read.
    ///
    /// ffmpeg shortens the file and writes the frame in two steps, so a read that lands between them
    /// holds the front of a picture and no end. Reading it again a millisecond later costs nothing
    /// next to the picture a page would go without, which is why this reads rather than gives up at
    /// the first half frame.
    /// </summary>
    public byte[]? Read(int videoPkid)
    {
        var path = FramePathOf(videoPkid);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(RetryDelay);
            }

            if (TryRead(path) is { } frame)
            {
                return frame;
            }
        }

        return null;
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = new byte[stream.Length];
            stream.ReadExactly(frame);
            return IsWholePicture(frame) ? frame : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>When the frame was last written, to send a new one only when there is one.</summary>
    public DateTime LastWrite(int videoPkid) =>
        File.GetLastWriteTimeUtc(FramePathOf(videoPkid));

    /// <summary>A stopped live leaves no frame behind: the page must not show it as if it were on air.</summary>
    public void Forget(int videoPkid)
    {
        Discard(FramePathOf(videoPkid));
        Discard(PendingPathOf(videoPkid));
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
}
