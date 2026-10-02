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
    private readonly string _path;
    private int _written;

    public LivePreviewFrameFileTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDirectory = Path.Combine(Path.GetTempPath(), "orbis-preview", Guid.NewGuid().ToString("N"));
        _frames = new LivePreviewFrames(OrbisRuntimeOptions.Resolve(
            [OrbisRuntimeOptions.DataDirectoryCommandLinePrefix + _dataDirectory],
            new Dictionary<string, string?>()));

        // Where a live in progress writes: the streamer asks for it once, when the transcode starts.
        _path = _frames.PathOf(VideoPkid);
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
    public void Latest_AnswersTheFrameFfmpegHasWritten()
    {
        var frame = WriteFrame();

        var read = _frames.Latest(VideoPkid);

        Assert.Equal(frame, read?.Bytes);

        // The whole picture, markers first and last: what every frame the preview serves has to be.
        Assert.Equal(0xFF, read!.Bytes[0]);
        Assert.Equal(0xD8, read.Bytes[1]);
        Assert.Equal(0xFF, read.Bytes[^2]);
        Assert.Equal(0xD9, read.Bytes[^1]);
    }

    [Fact]
    public void Latest_IsNothingWhenTheLiveHasNotWrittenAFrame()
    {
        Assert.Null(_frames.Latest(VideoPkid));
    }

    [Fact]
    public void Latest_GoesOnWithTheLastWholePictureWhenTheNextOneIsCaughtHalfWritten()
    {
        // ffmpeg shortens the file before the next frame goes in, so a reader that catches it
        // halfway holds the front of a picture and no end. Half a picture is never what a page is
        // handed - what it shows as a torn or grey rectangle - so the read is made again, and until
        // it succeeds the page goes on with the picture before it rather than without one.
        var frame = WriteFrame();
        var held = _frames.Latest(VideoPkid);
        Assert.NotNull(held);

        File.WriteAllBytes(_path, frame[..(frame.Length / 2)]);

        Assert.Equal(held.Stamp, _frames.Latest(VideoPkid)?.Stamp);

        WriteFrame();

        Assert.NotEqual(held.Stamp, _frames.Latest(VideoPkid)?.Stamp);
    }

    [Fact]
    public void Latest_IsNothingForAnEmptyFile()
    {
        // What an interrupted write leaves behind, and a picture no page can draw either.
        File.WriteAllBytes(_path, []);

        Assert.Null(_frames.Latest(VideoPkid));
    }

    [Fact]
    public void TheStampMovesWithThePictureAndNotWithTheWrite()
    {
        // The stamp is what tells two pictures apart, so a page is never handed the one it is
        // already holding. It counts the pictures this side has seen rather than naming the write
        // that produced them: the write time of a file ffmpeg keeps open for writing is not
        // something to trust frame by frame on Windows, and a stamp that does not move is a preview
        // frozen on a single frame of a live that is writing thirty of them a second.
        var first = WriteFrame();

        var held = _frames.Latest(VideoPkid);
        Assert.NotNull(held);
        Assert.False(string.IsNullOrEmpty(held.Stamp));

        // The same picture written again - a static scene, a frame ffmpeg found nothing to change -
        // is not a new picture, and the stamp that says so is what keeps the page from drawing it.
        File.WriteAllBytes(_path, first);

        Assert.Equal(held.Stamp, _frames.Latest(VideoPkid)?.Stamp);

        var second = WriteFrame();

        var moved = _frames.Latest(VideoPkid);
        Assert.NotEqual(held.Stamp, moved?.Stamp);
        Assert.Equal(second, moved?.Bytes);
    }

    [Fact]
    public async Task NextAsync_AnswersWithThePictureAfterTheOneThePageHolds()
    {
        WriteFrame();

        var held = _frames.Latest(VideoPkid);
        var second = WriteFrame();

        var next = await _frames.NextAsync(VideoPkid, held!.Stamp, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(second, next?.Bytes);
        Assert.NotEqual(held.Stamp, next?.Stamp);
    }

    [Fact]
    public async Task NextAsync_AnswersWithThePictureEvenWhenThePageHeldNothing()
    {
        // A page that has just opened holds no stamp, and must be given the picture that is already
        // there rather than wait for the next one: there is nothing to wait for as far as it knows.
        var frame = WriteFrame();

        var next = await _frames.NextAsync(VideoPkid, null, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(frame, next?.Bytes);
    }

    [Fact]
    public async Task NextAsync_WaitsForThePictureTheLiveIsWriting()
    {
        // The whole point of the wait: a page that asks for the picture it does not have is answered
        // when ffmpeg writes one, instead of on a timer of the page's own that drifts against the
        // clock ffmpeg writes on. The picture that comes back has to be the one that was written
        // while the request was open, which is a picture no other question could have been about.
        var first = WriteFrame();
        var held = _frames.Latest(VideoPkid);
        var second = NextFrame();

        var waiting = _frames.NextAsync(VideoPkid, held!.Stamp, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(80);
        File.WriteAllBytes(_path, second);

        var next = await waiting;

        Assert.Equal(second, next?.Bytes);
        Assert.NotEqual(first, next?.Bytes);
    }

    [Fact]
    public async Task NextAsync_AnswersNothingWhenTheLiveWritesNothing()
    {
        // A live that has ended, or a source that has nothing to give, is not a reason to hold a
        // page: the wait is bounded and the answer says there is nothing new.
        WriteFrame();
        var held = _frames.Latest(VideoPkid);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var next = await _frames.NextAsync(VideoPkid, held!.Stamp, TimeSpan.FromMilliseconds(80), CancellationToken.None);
        clock.Stop();

        Assert.Null(next);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(60), $"it answered after {clock.Elapsed}");
    }

    [Fact]
    public void Forget_TakesTheFrameAndTheOneTheRenameNeverMoved()
    {
        WriteFrame();
        Assert.NotNull(_frames.Latest(VideoPkid));

        var pending = $"{_path}.tmp";
        File.WriteAllBytes(pending, NextFrame());

        _frames.Forget(VideoPkid);

        Assert.False(File.Exists(_path));
        Assert.False(File.Exists(pending));

        // A live that is over leaves nothing behind to show: the page must not go on drawing the
        // last frame of a live that is not on air.
        Assert.Null(_frames.Latest(VideoPkid));
    }

    [Fact]
    public void PathOf_TakesAwayWhatTheRenameLeftBehind()
    {
        WriteFrame();
        var pending = $"{_path}.tmp";
        File.WriteAllBytes(pending, NextFrame());

        Assert.Equal(_path, _frames.PathOf(VideoPkid));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void PathOf_MakesTheFirstPictureOfTheNextLiveOneThePageHasNotSeen()
    {
        // A live that starts again on the row writes its own first picture: the last picture of the
        // live before it is not one this live wrote. The stamp is a fresh one all the same - a stamp
        // that could come up again for the row would tell a page that is still holding the first
        // stamp of the previous live that it already has the first frame of this one.
        WriteFrame();
        var before = _frames.Latest(VideoPkid);

        _frames.PathOf(VideoPkid);
        Assert.Null(_frames.Latest(VideoPkid));

        var second = WriteFrame();
        var after = _frames.Latest(VideoPkid);

        Assert.Equal(second, after?.Bytes);
        Assert.NotEqual(before?.Stamp, after?.Stamp);
    }

    /// <summary>Writes a frame over the one ffmpeg would have written, and answers what it wrote.</summary>
    private byte[] WriteFrame()
    {
        var frame = NextFrame();
        File.WriteAllBytes(_path, frame);
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
