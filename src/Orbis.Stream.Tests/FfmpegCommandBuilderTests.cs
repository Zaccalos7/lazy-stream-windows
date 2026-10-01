using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class FfmpegCommandBuilderTests
{
    private static VideoSettingEntity Setting() => new()
    {
        Id = 1,
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 5_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 },
        VideoSettingsOptions =
        [
            new VideoSettingsOptionEntity { Key = "preset", Value = "ultrafast" },
            new VideoSettingsOptionEntity { Key = "tune", Value = "zerolatency" }
        ]
    };

    private static MediaProbeResult Probe(bool hasAudio = true, double frameRate = 30d) =>
        new(1920, 1080, frameRate, hasAudio, hasAudio ? 2 : 0, 120d);

    [Fact]
    public void Build_UsesTheVideoSettingAsEncoderConfiguration()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), Setting())));

        Assert.Contains("-readrate 1 ", command, StringComparison.Ordinal);
        Assert.Contains("-i /videos/clip.mp4", command, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", command, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt yuv420p", command, StringComparison.Ordinal);
        Assert.Contains("-f flv", command, StringComparison.Ordinal);
        Assert.Contains("-b:v 5000000", command, StringComparison.Ordinal);
        Assert.Contains("-c:a aac", command, StringComparison.Ordinal);
        Assert.Contains("-b:a 128000", command, StringComparison.Ordinal);
        Assert.Contains("-ar 44100", command, StringComparison.Ordinal);
        Assert.Contains("-ac 2", command, StringComparison.Ordinal);
        Assert.Contains("-preset ultrafast", command, StringComparison.Ordinal);
        Assert.Contains("-tune zerolatency", command, StringComparison.Ordinal);
        Assert.EndsWith(" rtmp://ingest/live/key", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewOfAFileIsThirtySharpFramesASecond()
    {
        // The picture of the file, slowed down and shrunk inside the graph, and written as the light
        // picture: one file overwritten over and over, a temporary one renamed over it so the page
        // never reads half a frame, and no audio rides along with it.
        var command = FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), Setting(), PreviewPath: "/data/preview/7.jpg"));

        var text = string.Join(' ', command);

        // The scale of the setting comes first: the preview gets the same picture the live does,
        // and shrinks it on its own.
        Assert.Contains("fps=30,scale=w='min(640,iw)':h=-2", text, StringComparison.Ordinal);
        Assert.Contains("-an -sn -dn", text, StringComparison.Ordinal);
        Assert.Contains("-c:v mjpeg -q:v 6", text, StringComparison.Ordinal);
        Assert.Contains("-f image2 -update 1 -atomic_writing 1 /data/preview/7.jpg", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30d, 2, 60)]
    [InlineData(29.97d, 2, 59)]
    [InlineData(25d, 3, 75)]
    public void Build_ComputesGopAsFrameRateTimesGopSize(double frameRate, int gopSize, int expectedGop)
    {
        var setting = Setting();
        setting.GopSize = gopSize;

        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(frameRate: frameRate), setting)));

        Assert.Contains($"-g {expectedGop}", command, StringComparison.Ordinal);
        Assert.Contains($"-r {frameRate.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SkipsAudioFlagsWhenTheInputHasNoAudioTrack()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(hasAudio: false), Setting())));

        Assert.Contains("-map 0:v:0", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:a", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-map 0:a:0?", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\PS4 live di ""me"".mp4")]
    [InlineData(@"C:\Video\")]
    public void Build_PassesTheInputPathAsOneVerbatimArgument(string inputPath)
    {
        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest(inputPath, "rtmp://ingest/live/key", Probe(), Setting()));

        Assert.Equal(inputPath, arguments[arguments.ToList().IndexOf("-i") + 1]);
    }

    [Theory]
    [InlineData("rtmp://ingest/live", "key", "rtmp://ingest/live/key")]
    [InlineData("rtmp://ingest/live/", "key", "rtmp://ingest/live/key")]
    public void BuildStreamingUrl_JoinsWithASingleSeparator(string url, string key, string expected)
    {
        Assert.Equal(expected, FfmpegCommandBuilder.BuildStreamingUrl(url, key));
    }

    [Fact]
    public void Build_ScalesToTheResolutionOfTheSetting()
    {
        var setting = Setting();
        setting.VideoWidth = 1280;
        setting.VideoHeight = 720;

        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), setting));

        Assert.Equal("scale=1280:720", arguments[arguments.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Build_RoundsTheScaleDownToAnEvenFrameSize()
    {
        var setting = Setting();
        setting.VideoWidth = 1281;
        setting.VideoHeight = 721;

        var arguments = FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), setting));

        Assert.Equal("scale=1280:720", arguments[arguments.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void Build_LeavesTheResolutionOfTheSourceAloneWhenTheSettingHasNone()
    {
        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(), Setting())));

        Assert.DoesNotContain("-vf", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TakesTheFrameRateOfTheSettingOverTheOneOfTheSource()
    {
        var setting = Setting();
        setting.FrameRate = 60d;

        var command = string.Join(' ', FfmpegCommandBuilder.Build(
            new FfmpegStreamRequest("/videos/clip.mp4", "rtmp://ingest/live/key", Probe(frameRate: 23.976d), setting)));

        Assert.Contains("-r 60", command, StringComparison.Ordinal);
        Assert.Contains("-g 120", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOutput_FallsBackToWhatTheSourceCarries()
    {
        var output = FfmpegCommandBuilder.ResolveOutput(Setting(), Probe(frameRate: 23.976d));

        Assert.Equal(1920, output.Width);
        Assert.Equal(1080, output.Height);
        Assert.Equal(23.976d, output.FrameRate);
    }

    [Fact]
    public void ResolveOutput_AnswersWhatTheCommandLineAsksFor()
    {
        var setting = Setting();
        setting.VideoWidth = 854;
        setting.VideoHeight = 481;
        setting.FrameRate = 24d;

        var output = FfmpegCommandBuilder.ResolveOutput(setting, Probe(frameRate: 60d));

        Assert.Equal(854, output.Width);
        Assert.Equal(480, output.Height);
        Assert.Equal(24d, output.FrameRate);
        Assert.Equal("scale=854:480", FfmpegCommandBuilder.ScaleFilter(setting));
    }
}

public sealed class FfmpegCompositionTests
{
    private static VideoSettingEntity Setting() => new()
    {
        Id = 1,
        Title = "test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 5_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 }
    };

    private static FfmpegCompositionItem Screen(string target) =>
        new(SourceKind.Screen, target, 0, 0, 1920, 1080, AudioEnabled: false);

    private static FfmpegCompositionItem Camera(int x, int y, int width, int height, bool audio = false) =>
        new(SourceKind.Camera, "video=Integrated Camera", x, y, width, height, audio);

    private static FfmpegCompositionItem Microphone() =>
        new(SourceKind.Microphone, "audio=Microphone", 0, 0, 0, 0, true);

    private static FfmpegCompositionRequest Request(
        IReadOnlyList<FfmpegCompositionItem> items,
        int width = 1920,
        int height = 1080,
        string? previewPath = null) =>
        new(items, "rtmp://ingest/live/key", Setting(), width, height, CanvasFrameRate: 30d, PreviewPath: previewPath);

    [Fact]
    public void ThePreviewOfACanvasIsThirtySharpFramesASecond()
    {
        // The composed picture is split in two: the whole of it goes on air, and the copy is shrunk
        // and slowed down inside the graph, before it is written as the light picture.
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Camera(0, 0, 640, 480)], previewPath: "/data/preview/7.jpg"));

        var graph = GraphOf(command);

        Assert.Contains("split=2", graph, StringComparison.Ordinal);
        Assert.Contains("fps=30,scale=w='min(640,iw)':h=-2", graph, StringComparison.Ordinal);

        // It is one file overwritten over and over: a temporary one renamed over it, so the page
        // never reads half a frame, and no audio rides along with it.
        var text = string.Join(' ', command);

        Assert.Contains("-an -sn -dn -c:v mjpeg -q:v 6", text, StringComparison.Ordinal);
        Assert.Contains("-f image2 -update 1 -atomic_writing 1 /data/preview/7.jpg", text, StringComparison.Ordinal);
    }

    private static string GraphOf(IReadOnlyList<string> command) =>
        command[command.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void Desktop_WebcamAndMicrophone_ComposeThroughOneGraph()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(1400, 700, 480, 270), Microphone()]));

        var text = string.Join(' ', command);

        // Each capture device is opened with the device ffmpeg knows it by.
        Assert.Contains("-f gdigrab -framerate 30 -i desktop", text, StringComparison.Ordinal);
        Assert.Contains("-f dshow -i video=Integrated Camera", text, StringComparison.Ordinal);
        Assert.Contains("-f dshow -i audio=Microphone", text, StringComparison.Ordinal);

        // Every picture is scaled into the rectangle it was dropped on.
        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=decrease", text, StringComparison.Ordinal);
        Assert.Contains("scale=480:270:force_original_aspect_ratio=decrease", text, StringComparison.Ordinal);

        // And the result is one video and one audio stream, not one per source.
        Assert.Equal(2, command.Count(argument => argument == "-map"));
        Assert.Contains("-map [orbisv] -map [orbisa]", text, StringComparison.Ordinal);
        Assert.Contains("overlay=1400:700", text, StringComparison.Ordinal);
        Assert.Contains("[2:a]asetpts=PTS-STARTPTS", text, StringComparison.Ordinal);
        Assert.EndsWith("rtmp://ingest/live/key", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanvasIsABlankFrameOfTheSceneSize()
    {
        // A webcam alone in a corner stays in the corner: nothing is stretched to be the background.
        var graph = GraphOf(FfmpegCommandBuilder.BuildComposition(
            Request([Camera(1400, 700, 480, 270)], width: 1280, height: 720)));

        Assert.StartsWith("color=c=black:s=1280x720:r=30[canvas];", graph, StringComparison.Ordinal);
        Assert.Contains("[canvas][tile0]overlay=1400:700", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("scale=1280:720", graph, StringComparison.Ordinal);
        Assert.EndsWith("realtime[orbisv]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTileStartsItsClockAtZero()
    {
        // gdigrab and dshow stamp frames with the wall clock; the blank canvas starts at zero.
        var graph = GraphOf(FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(0, 0, 640, 480), Microphone()])));

        Assert.Contains("[0:v]setpts=PTS-STARTPTS,", graph, StringComparison.Ordinal);
        Assert.Contains("[1:v]setpts=PTS-STARTPTS,", graph, StringComparison.Ordinal);
        Assert.Contains("[2:a]asetpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void AMonitorIsTheDesktopCroppedToItsRectangle()
    {
        var target = MonitorTarget.Of(-1920, 0, 1920, 1080);
        var text = string.Join(' ', FfmpegCommandBuilder.BuildComposition(Request([Screen(target)])));

        Assert.Contains(
            "-f gdigrab -framerate 30 -offset_x -1920 -offset_y 0 -video_size 1920x1080 -i desktop",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInputIsOpenedBeforeTheGraph()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(0, 0, 640, 480), Microphone()]));

        var graphAt = command.ToList().IndexOf("-filter_complex");

        // ffmpeg rejects a graph that names an input it has not been told about yet.
        Assert.True(command.ToList().LastIndexOf("-i") < graphAt);
        Assert.Equal(3, command.Count(argument => argument == "-i"));
    }

    [Fact]
    public void AScreenCarriesNoSound()
    {
        // gdigrab captures no audio at all, so a tile with the sound on must not put one in the
        // mix: the mixed stream would be silent and the failure would only show on the platform.
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop") with { AudioEnabled = true }]));

        var text = string.Join(' ', command);

        Assert.DoesNotContain("amix", text, StringComparison.Ordinal);
        Assert.DoesNotContain("anull", text, StringComparison.Ordinal);
        Assert.Equal(1, command.Count(argument => argument == "-map"));
        Assert.DoesNotContain("-c:a", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraOpenedForItsPictureCarriesNoSound()
    {
        // video=… alone has no audio stream: [n:a] on it fails the whole graph.
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Camera(0, 0, 640, 480, audio: true)]));

        Assert.DoesNotContain("[0:a]", string.Join(' ', command), StringComparison.Ordinal);
        Assert.Equal(1, command.Count(argument => argument == "-map"));
    }

    [Fact]
    public void TwoSoundsAreMixed()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request(
            [
                new(SourceKind.File, "/videos/intro.mp4", 0, 0, 1920, 1080, true),
                Camera(0, 0, 640, 480),
                Microphone()
            ]));

        Assert.Contains(
            "[sound0][sound2]amix=inputs=2:dropout_transition=0",
            string.Join(' ', command),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMicrophoneIsHeardAndNotSeen()
    {
        var graph = GraphOf(FfmpegCommandBuilder.BuildComposition(
            Request([Microphone(), Camera(0, 0, 640, 480)])));

        Assert.DoesNotContain("[0:v]", graph, StringComparison.Ordinal);
        Assert.Contains("[0:a]asetpts", graph, StringComparison.Ordinal);
        Assert.Contains("[sound0]anull[orbisa]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileIsPacedAndSeeked()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request(
            [
                new(SourceKind.File, "/videos/intro.mp4", 0, 0, 1920, 1080, true),
                Camera(1400, 700, 480, 270)
            ]) with { ResumeFrom = TimeSpan.FromSeconds(12) });

        var text = string.Join(' ', command);

        Assert.Contains("-ss 12 -thread_queue_size 1024 -readrate 1 -i /videos/intro.mp4", text, StringComparison.Ordinal);

        // A capture device cannot be seeked into, so the position is only given to the file.
        Assert.DoesNotContain("-f dshow -ss", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEncoderIsTheSameOneAFileWouldUse()
    {
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(0, 0, 640, 480), Microphone()]));

        var text = string.Join(' ', command);

        Assert.Contains("-c:v libx264", text, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt yuv420p", text, StringComparison.Ordinal);
        Assert.Contains("-f flv", text, StringComparison.Ordinal);
        Assert.Contains("-b:v 5000000", text, StringComparison.Ordinal);
        Assert.Contains("-g 60", text, StringComparison.Ordinal);
        Assert.Contains("-ar 44100", text, StringComparison.Ordinal);

        // The composed picture is already the size of the canvas: a -vf here would resize the
        // result of the composition.
        Assert.DoesNotContain("-vf", command);
    }

    [Fact]
    public void SettingFrameRate_WinsOverTheCanvasOne()
    {
        var setting = Setting();
        setting.FrameRate = 24d;
        var request = Request([Screen("desktop")]) with { Setting = setting };

        var command = string.Join(' ', FfmpegCommandBuilder.BuildComposition(request));

        Assert.Contains("-f gdigrab -framerate 24", command, StringComparison.Ordinal);
        Assert.Contains("color=c=black:s=1920x1080:r=24", command, StringComparison.Ordinal);
        Assert.Contains("-r 24", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TileRectanglesAreRoundedDownToEvenPixels()
    {
        // yuv420p halves the frame in both directions, so an odd rectangle is not a small inaccuracy:
        // it is a filter that fails to initialise on the first frame.
        var command = FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(1411, 703, 481, 271)]));

        Assert.Contains("scale=480:270", string.Join(' ', command), StringComparison.Ordinal);
        Assert.Contains("overlay=1410:702", string.Join(' ', command), StringComparison.Ordinal);
    }

    [Fact]
    public void SourcesAreStackedInTheOrderTheyWereDropped()
    {
        var graph = GraphOf(FfmpegCommandBuilder.BuildComposition(
            Request([Screen("desktop"), Camera(0, 0, 640, 480), Camera(1280, 0, 640, 480)])));

        // The second camera goes over the first, and the first over the desktop: the order the user
        // arranged them in is the order they are drawn in.
        var desktop = graph.IndexOf("[canvas][tile0]overlay=0:0", StringComparison.Ordinal);
        var first = graph.IndexOf("[stack0][tile1]overlay=0:0", StringComparison.Ordinal);
        var second = graph.IndexOf("[stack1][tile2]overlay=1280:0", StringComparison.Ordinal);
        Assert.True(desktop > 0);
        Assert.True(first > desktop);
        Assert.True(second > first);
    }

    [Fact]
    public void ACompositionWithNoPictureIsRejected()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => FfmpegCommandBuilder.BuildComposition(Request([Microphone()])));

        Assert.Contains("picture", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACompositionWithNoSizeIsRejected()
    {
        var failure = Assert.Throws<ArgumentException>(
            () => FfmpegCommandBuilder.BuildComposition(Request([Screen("desktop")], width: 0, height: 0)));

        Assert.Contains("size", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATileWithNoRectangleIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => FfmpegCommandBuilder.BuildComposition(Request([Camera(0, 0, 0, 0)])));
    }
}

public sealed class SourceCatalogTests
{
    [Fact]
    public void DeviceList_OfFfmpeg5_NamesItsOwnKind()
    {
        const string list = """
            [dshow @ 000001d2a4c5e3c0] "Integrated Camera" (video)
            [dshow @ 000001d2a4c5e3c0]   Alternative name "@device_pnp_\\?\usb#vid_04f2"
            [dshow @ 000001d2a4c5e3c0] "Microfono (Realtek(R) Audio)" (audio)
            [dshow @ 000001d2a4c5e3c0]   Alternative name "@device_cm_{33D9A762}"
            [dshow @ 000001d2a4c5e3c0] "OBS Virtual Camera" (none)
            dummy: Immediate exit requested
            """;

        var options = CameraSourceProvider.Parse(list);

        Assert.Collection(
            options,
            camera =>
            {
                Assert.Equal(SourceKind.Camera, camera.Kind);
                Assert.Equal("Integrated Camera", camera.Name);
                Assert.Equal("video=Integrated Camera", camera.Target);
            },
            microphone =>
            {
                Assert.Equal(SourceKind.Microphone, microphone.Kind);
                Assert.Equal("audio=Microfono (Realtek(R) Audio)", microphone.Target);
            });
    }

    [Fact]
    public void DeviceList_OfFfmpeg4_IsReadBySection()
    {
        const string list = """
            [dshow @ 0000020] DirectShow video devices (some may be both video and audio devices)
            [dshow @ 0000020]  "USB Camera"
            [dshow @ 0000020]     Alternative name "@device_pnp_x"
            [dshow @ 0000020] DirectShow audio devices
            [dshow @ 0000020]  "Microphone (USB Camera)"
            [dshow @ 0000020]     Alternative name "@device_cm_y"
            """;

        var options = CameraSourceProvider.Parse(list);

        Assert.Equal(["video=USB Camera", "audio=Microphone (USB Camera)"], options.Select(option => option.Target));
    }

    [Fact]
    public void MonitorTarget_RoundTrips()
    {
        var target = MonitorTarget.Of(-1280, -200, 1280, 1024);

        Assert.True(MonitorTarget.TryParse(target, out var left, out var top, out var width, out var height));
        Assert.Equal((-1280, -200, 1280, 1024), (left, top, width, height));
        Assert.False(MonitorTarget.TryParse("desktop", out _, out _, out _, out _));
    }
}
