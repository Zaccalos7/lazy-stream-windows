using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Streaming;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>
/// The file the preview of a live is read from, and what a page is handed when it asks. No ffmpeg
/// here on purpose: both things that went wrong on a live are about the file rather than about the
/// transcode. The first is the frame ffmpeg was told to rename on top of the one before it, which
/// Windows refuses as soon as a scanner holds the file for a moment, so the preview of every live
/// stayed on the frame it started with. The second is a frame read while ffmpeg is writing it.
/// </summary>
public sealed class LivePreviewFrameFileTests : IDisposable
{
    private const int VideoPkid = 169;

    private readonly ITestOutputHelper _output;
    private readonly string _dataDirectory;
    private readonly LivePreviewFrames _frames;
    private int _written;

    public LivePreviewFrameFileTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDirectory = Path.Combine(Path.GetTempPath(), "orbis-preview", Guid.NewGuid().ToString("N"));
        _frames = new LivePreviewFrames(OrbisRuntimeOptions.Resolve(
            [OrbisRuntimeOptions.DataDirectoryCommandLinePrefix + _dataDirectory],
            new Dictionary<string, string?>()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException exception)
        {
            _output.WriteLine($"The data directory of the test is still there: {exception.Message}");
        }
    }

    [Fact]
    public void PathOf_IsTheJpegOfTheRowInsideThePreviewFolder()
    {
        var path = _frames.PathOf(VideoPkid);

        Assert.Equal(Path.Combine(_dataDirectory, "preview", $"{VideoPkid}.jpg"), path);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Read_AnswersTheFrameFfmpegHasWritten()
    {
        var frame = WriteFrame();

        var read = _frames.Read(VideoPkid);

        Assert.Equal(frame, read);

        // The whole picture, markers first and last: what every frame the preview serves has to be.
        Assert.Equal(0xFF, read![0]);
        Assert.Equal(0xD8, read[1]);
        Assert.Equal(0xFF, read[^2]);
        Assert.Equal(0xD9, read[^1]);
    }

    [Fact]
    public void Read_IsNothingWhenTheLiveHasNotWrittenAFrame()
    {
        Assert.Null(_frames.Read(VideoPkid));

        _frames.PathOf(VideoPkid);

        Assert.Null(_frames.Read(VideoPkid));
    }

    [Fact]
    public void Read_IsNothingForAFrameCaughtHalfWritten()
    {
        // ffmpeg shortens the file before the next frame goes in, so a reader that catches it
        // halfway holds the front of a picture and no end. The page is answered with nothing rather
        // than with something it cannot draw, and asks again a frame later - at thirty frames a
        // second that is a frame nobody sees missing.
        var frame = WriteFrame();

        File.WriteAllBytes(_frames.PathOf(VideoPkid), frame[..(frame.Length / 2)]);

        Assert.Null(_frames.Read(VideoPkid));

        WriteFrame();

        Assert.NotNull(_frames.Read(VideoPkid));
    }

    [Fact]
    public void Read_IsNothingForAnEmptyFile()
    {
        // What an interrupted write leaves behind, and a picture no page can draw either.
        File.WriteAllBytes(_frames.PathOf(VideoPkid), []);

        Assert.Null(_frames.Read(VideoPkid));
    }

    [Fact]
    public void Forget_TakesTheFrameAndTheOneTheRenameNeverMoved()
    {
        WriteFrame();
        var pending = $"{_frames.PathOf(VideoPkid)}.tmp";
        File.WriteAllBytes(pending, NextFrame());

        _frames.Forget(VideoPkid);

        Assert.False(File.Exists(_frames.PathOf(VideoPkid)));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void PathOf_TakesAwayWhatTheRenameLeftBehind()
    {
        WriteFrame();
        var pending = $"{_frames.PathOf(VideoPkid)}.tmp";
        File.WriteAllBytes(pending, NextFrame());

        Assert.Equal(_frames.PathOf(VideoPkid), _frames.PathOf(VideoPkid));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void LastWrite_IsTheMomentTheFrameWasWritten()
    {
        // A live that has written nothing yet has no frame and no moment: the endpoint never asks,
        // because the read comes back empty first.
        Assert.Equal(DateTime.FromFileTimeUtc(0), _frames.LastWrite(VideoPkid));

        WriteFrame();
        var written = _frames.LastWrite(VideoPkid);
        var first = File.ReadAllBytes(_frames.PathOf(VideoPkid));

        Assert.True(written > DateTime.MinValue);

        // The stamp is what tells two frames apart, so the endpoint can answer the page that already
        // holds this exact frame with a 304: it has to be the moment of the frame and nothing else.
        Thread.Sleep(20);
        var second = WriteFrame();

        Assert.True(_frames.LastWrite(VideoPkid) >= written);
        Assert.NotEqual(first, second);
    }

    /// <summary>Writes a frame over the one ffmpeg would have written, and answers what it wrote.</summary>
    private byte[] WriteFrame()
    {
        var frame = NextFrame();
        File.WriteAllBytes(_frames.PathOf(VideoPkid), frame);
        return frame;
    }

    /// <summary>
    /// A picture as ffmpeg writes one: the start-of-image marker, the segments that describe it, the
    /// compressed body, and the end-of-image marker. The body carries the row and the frame number,
    /// so two frames of a live are two different pictures instead of the same one twice.
    /// </summary>
    private byte[] NextFrame()
    {
        var number = _written++;
        var body = new byte[256];
        body[0] = (byte)VideoPkid;
        body[1] = (byte)(VideoPkid >> 8);
        body[2] = (byte)(number & 0xFF);
        body[3] = (byte)(number >> 8);

        return
        [
            0xFF, 0xD8,

            // APP0/JFIF: the segment ffmpeg puts in front of every picture it writes.
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,

            // SOF0: the size of the picture and the frame counter the live keeps.
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00,

            // SOS, the picture itself, and the end of it.
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            .. body,
            0xFF, 0xD9
        ];
    }
}