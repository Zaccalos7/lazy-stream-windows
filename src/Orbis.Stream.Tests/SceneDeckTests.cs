using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

/// <summary>
/// The switchboard of the scene deck (LiveTakeovers): what goes on air in place of the program of a
/// live, in which order, and when it has to make way. No ffmpeg here: the rules alone.
/// </summary>
public sealed class LiveTakeoversTests
{
    private const long History = 7;

    private static LiveTakeovers Open(out LiveChangeNotifier notifier)
    {
        notifier = new LiveChangeNotifier();
        var takeovers = new LiveTakeovers(notifier);
        takeovers.Open(History);
        return takeovers;
    }

    [Fact]
    public void A_live_that_is_not_running_takes_no_request()
    {
        var takeovers = new LiveTakeovers(new LiveChangeNotifier());

        Assert.False(takeovers.IsOpen(History));
        Assert.Null(takeovers.Request(History, TakeoverKind.Image, "brb.png", "BRB", 1, cut: true));
        Assert.False(takeovers.Resume(History));
        Assert.False(takeovers.HasPending(History));
    }

    [Fact]
    public void A_button_cuts_what_is_on_air_and_what_was_waiting()
    {
        var takeovers = Open(out _);
        var spot = takeovers.Request(History, TakeoverKind.Video, "spot.mp4", "spot.mp4", null, cut: false)!;
        Assert.True(takeovers.TryTake(History, out var onAir));
        Assert.Equal(spot, onAir);

        // A second spot waits for the first clip to end.
        takeovers.Request(History, TakeoverKind.Video, "spot2.mp4", "spot2.mp4", null, cut: false);
        Assert.False(takeovers.ShouldEnd(History, spot));

        // A button is a switch now: the clip on air ends and the spot that waited is dropped.
        var brb = takeovers.Request(History, TakeoverKind.Image, "brb.png", "BRB", 3, cut: true)!;
        Assert.True(takeovers.ShouldEnd(History, spot));
        Assert.True(takeovers.TryTake(History, out var next));
        Assert.Equal(brb, next);
        Assert.False(takeovers.HasPending(History));
        Assert.Equal(brb, takeovers.CurrentOf(History));
    }

    [Fact]
    public void A_picture_makes_way_for_a_spot_where_a_clip_would_not()
    {
        var takeovers = Open(out _);
        var picture = takeovers.Request(History, TakeoverKind.Image, "brb.png", "BRB", 1, cut: true)!;
        Assert.True(takeovers.TryTake(History, out _));
        Assert.False(takeovers.ShouldEnd(History, picture));

        // A picture would stay for ever: a spot asked meanwhile takes its place.
        takeovers.Request(History, TakeoverKind.Video, "spot.mp4", "spot.mp4", null, cut: false);
        Assert.True(takeovers.ShouldEnd(History, picture));
    }

    [Fact]
    public void Resume_ends_what_stands_in_and_drops_what_waited()
    {
        var takeovers = Open(out var notifier);
        var picture = takeovers.Request(History, TakeoverKind.Image, "brb.png", "BRB", 1, cut: true)!;
        var before = notifier.Version;
        Assert.True(takeovers.TryTake(History, out _));
        Assert.True(notifier.Version > before, "the pages are not told what went on air");

        takeovers.Request(History, TakeoverKind.Video, "spot.mp4", "spot.mp4", null, cut: false);
        Assert.True(takeovers.Resume(History));
        Assert.True(takeovers.ShouldEnd(History, picture));
        Assert.False(takeovers.HasPending(History));

        takeovers.BackToProgram(History);
        Assert.Null(takeovers.CurrentOf(History));
        Assert.False(takeovers.Resume(History));

        // The same button asked again is a new request, not the one that was ended.
        var again = takeovers.Request(History, TakeoverKind.Image, "brb.png", "BRB", 1, cut: true)!;
        Assert.True(takeovers.TryTake(History, out _));
        Assert.False(takeovers.ShouldEnd(History, again));
    }

    [Fact]
    public void The_close_of_an_old_run_leaves_the_new_one_alone()
    {
        var takeovers = new LiveTakeovers(new LiveChangeNotifier());
        var old = takeovers.Open(History);
        var current = takeovers.Open(History);

        takeovers.Close(History, old);
        Assert.True(takeovers.IsOpen(History));

        takeovers.Close(History, current);
        Assert.False(takeovers.IsOpen(History));
    }

    [Fact]
    public void A_file_on_air_or_waiting_is_in_use()
    {
        var takeovers = Open(out _);
        takeovers.Request(History, TakeoverKind.Image, "/media/brb.png", "BRB", 1, cut: true);
        Assert.True(takeovers.Uses("/media/brb.png"));
        Assert.True(takeovers.TryTake(History, out _));
        Assert.True(takeovers.Uses("/media/brb.png"));
        Assert.False(takeovers.Uses("/media/intro.mp4"));

        takeovers.BackToProgram(History);
        Assert.False(takeovers.Uses("/media/brb.png"));
    }

    [Fact]
    public void The_page_is_told_whether_what_is_on_air_stays()
    {
        Assert.True(LiveScene.Of(new Takeover(1, TakeoverKind.Image, "a.png", "BRB", 4, true))!.Holds);
        var clip = LiveScene.Of(new Takeover(2, TakeoverKind.Video, "a.mp4", "Intro", 5, true))!;
        Assert.Equal(("VIDEO", false, 5L), (clip.Kind, clip.Holds, clip.ButtonPkid!.Value));
        Assert.Null(LiveScene.Of(null));
    }

    [Fact]
    public void An_overlay_can_be_placed_in_scene_and_stopped()
    {
        var takeovers = Open(out _);
        var overlay = takeovers.RequestOverlay(
            History,
            buttonPkid: 42,
            path: "/media/test.gif",
            label: "Dancing Cat",
            kind: SceneButtonKind.Image,
            placement: "bottom-right",
            x: null,
            y: null,
            width: null,
            height: null,
            durationSeconds: 10);

        Assert.NotNull(overlay);
        Assert.Equal(42, overlay.ButtonPkid);
        Assert.True(takeovers.Uses("/media/test.gif"));

        var active = takeovers.ActiveOverlayOf(History);
        Assert.NotNull(active);
        Assert.Equal("Dancing Cat", active.Label);

        var scene = takeovers.LiveSceneOf(History);
        Assert.NotNull(scene);
        Assert.Equal("in_scene", scene.Mode);
        Assert.Equal("bottom-right", scene.Placement);
        Assert.Equal(10, scene.DurationSeconds);
        Assert.False(scene.Holds);

        var stopped = takeovers.StopOverlay(History, 42);
        Assert.True(stopped);
        Assert.Null(takeovers.ActiveOverlayOf(History));
        Assert.False(takeovers.Uses("/media/test.gif"));
    }

    [Fact]
    public void An_overlay_without_duration_holds_indefinitely()
    {
        var takeovers = Open(out _);
        takeovers.RequestOverlay(
            History,
            buttonPkid: 43,
            path: "/media/badge.png",
            label: "Watermark",
            kind: SceneButtonKind.Image,
            placement: "top-left",
            x: null,
            y: null,
            width: null,
            height: null,
            durationSeconds: null);

        var scene = takeovers.LiveSceneOf(History);
        Assert.NotNull(scene);
        Assert.True(scene.Holds);
        Assert.Equal("top-left", scene.Placement);
    }
}

/// <summary>The keys a scene button can be given (SceneHotkey), and what a file does on air.</summary>
public sealed class SceneHotkeyTests
{
    [Theory]
    [InlineData("Digit1")]
    [InlineData("KeyB")]
    [InlineData("F7")]
    [InlineData("F24")]
    [InlineData("Numpad3")]
    [InlineData("NumpadAdd")]
    [InlineData("Ctrl+Digit1")]
    [InlineData("Ctrl+Alt+Shift+KeyQ")]
    [InlineData("Shift+ArrowUp")]
    [InlineData("PageDown")]
    public void A_key_the_page_does_not_need_is_taken(string hotkey) => Assert.True(SceneHotkey.IsValid(hotkey));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Enter")]
    [InlineData("Escape")]
    [InlineData("Space")]
    [InlineData("Tab")]
    [InlineData("F5")]
    [InlineData("F12")]
    [InlineData("Ctrl+KeyR")]
    [InlineData("Ctrl+KeyW")]
    [InlineData("Alt+F4")]
    [InlineData("Shift+Ctrl+KeyA")]
    [InlineData("Ctrl+")]
    [InlineData("KeyBB")]
    [InlineData("F25")]
    [InlineData("MediaPlayPause")]
    [InlineData("Digit1; DROP TABLE")]
    public void A_key_the_page_needs_or_does_not_know_is_refused(string? hotkey) => Assert.False(SceneHotkey.IsValid(hotkey));

    [Theory]
    [InlineData("brb.png", SceneButtonKind.Image)]
    [InlineData("banner.GIF", SceneButtonKind.Image)]
    [InlineData("logo.webp", SceneButtonKind.Image)]
    [InlineData("intro.mp4", SceneButtonKind.Video)]
    [InlineData("loop.webm", SceneButtonKind.Video)]
    [InlineData("clip.MOV", SceneButtonKind.Video)]
    public void A_picture_stays_and_a_clip_goes_back(string file, SceneButtonKind kind) =>
        Assert.Equal(kind, SceneButtonService.KindOf(file));
}

/// <summary>The command lines of what goes on air in place of the program.</summary>
public sealed class SceneSwitchCommandTests
{
    private static VideoSettingEntity Setting(bool audio = true) => new()
    {
        Title = "Test",
        VideoCodec = 27,
        VideoCodecName = "libx264",
        PixelFormat = 0,
        VideoBitrate = 2_000_000,
        VideoFormat = "flv",
        GopSize = 2,
        AudioSetting = audio ? new AudioSettingEntity { AudioCodec = 86018, AudioBitrate = 128_000 } : null
    };

    [Theory]
    [InlineData(1920, 1080, 1280, 720, 0, 0, 1280, 720)]
    [InlineData(400, 400, 1920, 1080, 420, 0, 1080, 1080)]
    [InlineData(1920, 200, 1280, 720, 0, 294, 1280, 132)]
    [InlineData(1080, 1920, 1920, 1080, 656, 0, 608, 1080)]
    [InlineData(0, 0, 1280, 720, 0, 0, 1280, 720)]
    public void A_picture_is_fitted_whole_and_centred(
        int width, int height, int frameWidth, int frameHeight, int x, int y, int fittedWidth, int fittedHeight) =>
        Assert.Equal((x, y, fittedWidth, fittedHeight), FfmpegCommandBuilder.Contain(width, height, frameWidth, frameHeight));

    [Fact]
    public void A_picture_in_place_of_the_live_carries_a_silent_track_even_where_the_platform_needs_none()
    {
        var picture = new FfmpegCompositionItem(
            SourceKind.Overlay, "/media/brb.png", 0, 0, 1280, 720, AudioEnabled: false, Overlay: OverlayMedia.Still);
        var arguments = FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [picture], "rtmp://live.twitch.tv/app/key", Setting(audio: false), 1280, 720, 30,
            Profile: StreamPlatformProfile.Twitch, AlwaysSound: true));
        var line = string.Join(' ', arguments);

        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=44100", line, StringComparison.Ordinal);
        Assert.Contains("-map 1:a:0", line, StringComparison.Ordinal);
        Assert.Contains("-c:a aac", line, StringComparison.Ordinal);
        // A still is held, never looped, and the canvas has no end of its own.
        Assert.DoesNotContain("-stream_loop", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" -t ", line, StringComparison.Ordinal);

        // The program itself keeps the rule of the platform: Twitch takes a live with no sound.
        var program = string.Join(' ', FfmpegCommandBuilder.BuildComposition(new FfmpegCompositionRequest(
            [picture], "rtmp://live.twitch.tv/app/key", Setting(audio: false), 1280, 720, 30, Profile: StreamPlatformProfile.Twitch)));
        Assert.DoesNotContain("anullsrc", program, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_in_place_of_the_live_is_given_silence_only_when_it_has_no_sound()
    {
        var silent = new MediaProbeResult(1280, 720, 30, false, 0, 12);
        var sounding = new MediaProbeResult(1280, 720, 30, true, 2, 12);

        string Line(MediaProbeResult probe) => string.Join(' ', FfmpegCommandBuilder.Build(new FfmpegStreamRequest(
            "/media/intro.mp4", "rtmp://live.twitch.tv/app/key", probe, Setting(), Profile: StreamPlatformProfile.Twitch, AlwaysSound: true)));

        Assert.Contains("anullsrc", Line(silent), StringComparison.Ordinal);
        Assert.Contains("-shortest", Line(silent), StringComparison.Ordinal);
        Assert.DoesNotContain("anullsrc", Line(sounding), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bottom-right", 1920, 1080, 400, 300, 1488, 756, 400, 300)]
    [InlineData("bottom-left", 1920, 1080, 400, 300, 32, 756, 400, 300)]
    [InlineData("top-right", 1920, 1080, 400, 300, 1488, 24, 400, 300)]
    [InlineData("top-left", 1920, 1080, 400, 300, 32, 24, 400, 300)]
    [InlineData("center", 1920, 1080, 400, 300, 760, 390, 400, 300)]
    [InlineData("custom", 1920, 1080, 400, 300, 100, 200, 500, 400)]
    public void Overlay_placement_calculation_computes_correct_coordinates(
        string placement, int canvasW, int canvasH, int mediaW, int mediaH,
        int expectedX, int expectedY, int expectedW, int expectedH)
    {
        int? customX = placement == "custom" ? 100 : null;
        int? customY = placement == "custom" ? 200 : null;
        int? customW = placement == "custom" ? 500 : null;
        int? customH = placement == "custom" ? 400 : null;

        var (x, y, w, h) = FfmpegCommandBuilder.CalculateOverlayPlacement(
            placement, customX, customY, customW, customH, canvasW, canvasH, mediaW, mediaH);

        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Fact]
    public void Overlay_timeline_enable_is_included_in_filter_graph()
    {
        var composition = new List<FfmpegCompositionItem>
        {
            new(SourceKind.Direct, "/tmp/base.mp4", 0, 0, 1920, 1080, 1, 0),
            new(SourceKind.Overlay, "/tmp/overlay.png", 100, 100, 400, 300, 1, 1, TimelineEnable: "between(t,0,10)")
        };

        var filter = FfmpegCommandBuilder.BuildFilterGraph(1920, 1080, composition, hasAudio: false, audioNormalized: false);
        Assert.Contains(":enable='between(t,0,10)'", filter);
    }
}
