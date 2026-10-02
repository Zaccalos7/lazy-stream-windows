using Orbis.Stream.Core.Configuration;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// Where the light preview of each live is written: one JPEG per running session, named after the
/// row the session is registered under, overwritten by ffmpeg a few times a second. It lives in the
/// data directory, next to the database, and goes away with the session.
/// </summary>
public sealed class LivePreviewFrames
{
    private readonly string _directory;

    public LivePreviewFrames(OrbisRuntimeOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "preview");
    }

    /// <summary>The file ffmpeg writes the frames of a live to; the folder is made on the way.</summary>
    public string PathOf(int videoPkid)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, $"{videoPkid}.jpg");
    }

    /// <summary>The last frame of a live, or null when it has written none yet (or is not running).
    /// Read with every share mode, so ffmpeg can rename the next frame over it while it is read.</summary>
    public byte[]? Read(int videoPkid)
    {
        var path = Path.Combine(_directory, $"{videoPkid}.jpg");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = new byte[stream.Length];
            stream.ReadExactly(frame);
            return frame.Length == 0 ? null : frame;
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
        File.GetLastWriteTimeUtc(Path.Combine(_directory, $"{videoPkid}.jpg"));

    /// <summary>A stopped live leaves no frame behind: the page must not show it as if it were on air.</summary>
    public void Forget(int videoPkid)
    {
        try
        {
            File.Delete(Path.Combine(_directory, $"{videoPkid}.jpg"));
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
