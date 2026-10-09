using Microsoft.Extensions.Logging.Abstractions;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// The overlays of a layout on their way to ffmpeg: how each file is opened, how it is laid over
/// the canvas, and what it never does - decide the sound, the length, the clock or the row of a live.
/// </summary>
public sealed class OverlayCommandTests
{
    private static VideoSettingEntity Setting() => new()
    {
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 5_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    private static FfmpegCompositionRequest Request(params FfmpegCompositionItem[] items) =>
        new(items, "rtmp://ingest/live/key", Setting(), 1920, 1080, CanvasFrameRate: 30d);

    private static FfmpegCompositionItem Video(bool audio = false) =>
        new(SourceKind.File, "/videos/clip.mp4", 0, 0, 1920, 1080, audio);

    private static FfmpegCompositionItem Frame(OverlayMedia? media = null, bool audio = false) =>
        new(SourceKind.Overlay, "/overlays/frame-0123456789.png", 0, 0, 1920, 1080, audio, Overlay: media);

    private static readonly OverlayMedia Animated = new(true, "libvpx-vp9");

    private static string GraphOf(IReadOnlyList<string> command) =>
        command[command.ToList().IndexOf("-filter_complex") + 1];

    [Theory]
    [InlineData("png_pipe", "png", 0, false, false, null)]
    [InlineData("webp_pipe", "webp", 0, false, false, null)]
    [InlineData("image2", "mjpeg", 0, false, false, null)]
    [InlineData("gif", "gif", 1, false, false, null)]
    [InlineData("gif", "gif", 20, false, true, null)]
    [InlineData("apng", "apng", 0, false, true, null)]
    [InlineData("matroska,webm", "vp9", 0, true, true, "libvpx-vp9")]
    [InlineData("matroska,webm", "vp8", 0, true, true, "libvpx")]
    [InlineData("matroska,webm", "vp9", 0, false, true, null)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "qtrle", 60, false, true, null)]
    public void AStillIsOpenedOnceAndAnAnimationLoopsWithTheDecoderThatKeepsItsAlpha(
        string format, string codec, int frames, bool alpha, bool loop, string? decoder)
    {
        var duration = frames > 1 ? 2d : 0.1d;
        var media = OverlayMedia.Of(new MediaProbeResult(1920, 1080, 30, false, 0, duration, Format: format, VideoCodec: codec, Frames: frames, AlphaMode: alpha));

        Assert.Equal(new OverlayMedia(loop, decoder), media);
    }

    [Fact]
    public void ThePageReadsTheFormatTheCodecTheFramesAndTheAlphaOfAWebM()
    {
        const string json =
            """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "width": 1920, "height": 1080, "nb_frames": "120",
                  "avg_frame_rate": "30/1", "pix_fmt": "yuv420p", "tags": { "ALPHA_MODE": "1", "ENCODER": "Lavc libvpx-vp9" } }
              ],
              "format": { "format_name": "matroska,webm", "duration": "4.000000" }
            }
            """;

        var probe = new FfmpegProbe(new FfmpegToolLocator("ffmpeg", "ffprobe"), NullLogger<FfmpegProbe>.Instance).Parse(json, "overlay.webm");

        Assert.Equal("matroska,webm", probe.Format);
        Assert.Equal("vp9", probe.VideoCodec);
        Assert.Equal(120, probe.Frames);
        Assert.True(probe.AlphaMode);
        Assert.Equal(4d, probe.DurationSeconds);
    }

    [Fact]
    public void AStillIsDecodedOnceAndLaidWithItsAlpha()
    {
        var command = FfmpegCommandBuilder.BuildComposition(Request(Video(), Frame()));
        var text = string.Join(' ', command);
        var graph = GraphOf(command);

        // Opened as it is: the overlay filter holds its one picture for the whole live.
        Assert.Contains("-thread_queue_size 512 -i /overlays/frame-0123456789.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-stream_loop", text, StringComparison.Ordinal);

        // Multiplied by its alpha before the scale, stretched to its rectangle, laid at its corner.
        Assert.Contains("[1:v]setpts=PTS-STARTPTS,format=yuva420p,premultiply=inplace=1,scale=1920:1080,setsar=1[tile1]", graph, StringComparison.Ordinal);
        Assert.Contains("[stack0][tile1]overlay=0:0:format=auto:alpha=premultiplied[stack1]", graph, StringComparison.Ordinal);

        // The source under it is still filled and cropped the way it always was.
        Assert.Contains("[0:v]setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=increase", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnimationLoopsForAsLongAsTheLiveThroughTheDecoderItNeeds()
    {
        var text = string.Join(' ', FfmpegCommandBuilder.BuildComposition(Request(Video(), Frame(Animated))));

        // The decoder is an input option: it has to come before the file.
        Assert.Contains("-stream_loop -1 -c:v libvpx-vp9 -thread_queue_size 512 -i /overlays/frame-0123456789.png", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlayIsNeverSoughtIntoWhenTheLiveResumes()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request(Video(), Frame(Animated)) with { ResumeFrom = TimeSpan.FromSeconds(30) });
        var text = string.Join(' ', command);

        // The video carries on from where it stopped; the logo has no position to carry on from.
        Assert.Contains("-ss 30", text, StringComparison.Ordinal);
        var overlayInput = text.IndexOf("-i /overlays/", StringComparison.Ordinal);
        var lastSeek = text.LastIndexOf("-ss ", StringComparison.Ordinal);
        Assert.True(lastSeek < text.IndexOf("-i /videos/clip.mp4", StringComparison.Ordinal) && lastSeek < overlayInput);
    }

    [Fact]
    public void AnOverlayIsNeverHeard()
    {
        // A WebM overlay may well carry a track: asking the graph for it would mix it in.
        var graph = GraphOf(FfmpegCommandBuilder.BuildComposition(Request(Video(audio: true), Frame(Animated, audio: true))));

        Assert.Contains("[0:a]asetpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[1:a]", graph, StringComparison.Ordinal);
        Assert.False(FfmpegCommandBuilder.CarriesSound([Frame(Animated, audio: true)]));
    }

    [Fact]
    public void ACanvasOfFilesAndOverlaysIsPacedByTheRelayAlone()
    {
        // An overlay has no clock of its own: the canvas is still a canvas of files.
        var command = FfmpegCommandBuilder.BuildComposition(
            Request(Video(), Frame(Animated)) with
            {
                OutputUrl = "rtmp://live.twitch.tv/app/key",
                Profile = StreamPlatformProfile.Twitch
            });

        Assert.DoesNotContain("-readrate", string.Join(' ', command), StringComparison.Ordinal);
        Assert.DoesNotContain("realtime", GraphOf(command), StringComparison.Ordinal);
    }

    [Fact]
    public void WithADeviceOnTheCanvasAnAnimationIsReadAtItsOwnPace()
    {
        var camera = new FfmpegCompositionItem(SourceKind.Camera, "video=Cam", 0, 0, 1920, 1080, false);
        var text = string.Join(' ', FfmpegCommandBuilder.BuildComposition(Request(camera, Frame(Animated), Frame())));

        Assert.Contains("-readrate 1 -i /overlays/frame-0123456789.png", text, StringComparison.Ordinal);
        // A still has no pace to keep: it is one picture.
        Assert.Contains("-thread_queue_size 512 -i /overlays/frame-0123456789.png -filter_complex", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStillOfAnOverlayKeepsItsAlpha()
    {
        var text = string.Join(' ', FfmpegCommandBuilder.BuildSnapshot(SourceKind.Overlay, "/overlays/intro.mov", 960, Animated));

        Assert.Contains("-c:v libvpx-vp9 -thread_queue_size 512 -i /overlays/intro.mov", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-stream_loop", text, StringComparison.Ordinal);
        Assert.Contains("-c:v png pipe:1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mjpeg", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACanvasStandsOnItsFirstSourceEvenWhenAnOverlayIsUnderIt()
    {
        var backdrop = new VideoEntity { Pkid = 1, Name = "Backdrop", SourceKind = SourceKind.Overlay };
        var video = new VideoEntity { Pkid = 2, Name = "Video", SourceKind = SourceKind.File };
        var microphone = new VideoEntity { Pkid = 3, Name = "Mic", SourceKind = SourceKind.Microphone };

        Assert.Same(video, SceneRows.BaseOf([backdrop, video, microphone]));
        // A layout of overlays alone stands on its first one.
        Assert.Same(backdrop, SceneRows.BaseOf([microphone, backdrop]));
    }

    [Theory]
    [InlineData("frame-0123456789.png", "frame.png")]
    [InlineData("my-overlay-v2-abcdef0123.webm", "my-overlay-v2.webm")]
    [InlineData("hand-made.png", "hand-made.png")]
    public void TheLibraryShowsTheNameAFileWasAddedWith(string stored, string label)
    {
        Assert.Equal(label, OverlayLibrary.LabelOf(stored));
    }

    [Theory]
    [InlineData("Overlay (finale) #2.png", "Overlay-finale-2")]
    [InlineData("../../etc/passwd.png", "passwd")]
    [InlineData("€€€.png", "overlay")]
    [InlineData("Überlagerung.webm", "Überlagerung")]
    public void TheStoredNameIsSafeOnAnyDiskAndInAnyUrl(string label, string stem)
    {
        Assert.Equal(stem, OverlayLibrary.StemOf(label));
    }

    [Theory]
    [InlineData("frame.PNG", true)]
    [InlineData("anim.webm", true)]
    [InlineData("intro.mov", true)]
    [InlineData("notes.txt", false)]
    [InlineData("clip.mkv", false)]
    [InlineData(null, false)]
    public void OnlyPicturesAndVideosWithAlphaJoinTheLibrary(string? name, bool accepted)
    {
        Assert.Equal(accepted, OverlayLibrary.IsOverlayFile(name));
    }
}
