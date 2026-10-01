using System.Globalization;
using System.Text;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Streaming;

/// <summary>Everything needed to transcode one file into the live destination.</summary>
public sealed record FfmpegStreamRequest(
    string InputPath,
    string OutputUrl,
    MediaProbeResult Probe,
    VideoSettingEntity Setting,
    TimeSpan ResumeFrom = default,
    string? PreviewPath = null);

/// <summary>One source on the canvas, as the command line needs it.</summary>
public sealed record FfmpegCompositionItem(
    SourceKind Kind,
    string Target,
    int X,
    int Y,
    int Width,
    int Height,
    bool AudioEnabled);

/// <summary>
/// Everything needed to stream a canvas: the sources, where they sit, and the encoder setting they
/// all go through. The canvas size is answered before the command is built, because ffmpeg has to
/// be told it up front and the base source cannot be resized onto a size nobody has decided yet.
/// </summary>
public sealed record FfmpegCompositionRequest(
    IReadOnlyList<FfmpegCompositionItem> Items,
    string OutputUrl,
    VideoSettingEntity Setting,
    int CanvasWidth,
    int CanvasHeight,
    double CanvasFrameRate,
    TimeSpan ResumeFrom = default,
    string? PreviewPath = null);

/// <summary>
/// Translates the <c>FFmpegFrameRecorder</c> configuration of <c>StreamService</c> into the
/// equivalent ffmpeg command line. The Java version decoded every frame in Java and re-encoded
/// it with real time pacing; <c>-re</c> gives the same pacing to the ffmpeg process.
/// </summary>
public static class FfmpegCommandBuilder
{
    /// <summary>Where the composed picture leaves the graph, and the composed sound with it.</summary>
    private const string VideoLabel = "orbisv";

    private const string AudioLabel = "orbisa";

    private const string CanvasLabel = "canvas";

    /// <summary>The copy of the composed picture that goes to the preview instead of the live.</summary>
    private const string PreviewLabel = "orbisp";

    /// <summary>
    /// The preview is a picture to look at, not a second live: fifteen frames a second, small, as
    /// JPEG. It costs next to nothing next to the live encode, and it is what is on air (the
    /// composed canvas, the scaled file) rather than the source the page would otherwise replay.
    ///
    /// The size is what makes it read well: at 426 pixels wide the frame was stretched over a stage
    /// twice as wide, and a blurred mosaic moving in steps is worse to watch than a sharp picture
    /// moving smoothly. 640 is as wide as the stage, so nothing is scaled up, and fifteen frames a
    /// second is enough for the eye to read as motion. Encoding a frame this size costs a few
    /// milliseconds, so the preview stays a small slice of a core next to the live encode.
    /// </summary>
    private const string PreviewFilter = "fps=15,scale=w='min(640,iw)':h=-2";

    public static IReadOnlyList<string> Build(FfmpegStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setting = request.Setting;
        var probe = request.Probe;

        // The rate the encoder is asked for: what the setting asks for, otherwise the rate ffprobe
        // read from the file (25 is the fallback of the probe, kept for a probe that knows nothing).
        var output = ResolveOutput(setting, probe);
        var frameRate = output.FrameRate;

        var arguments = GlobalArguments();

        // Where to carry on from, before the input: as an input option ffmpeg seeks there and starts
        // reading immediately, where an output option would decode and throw the frames away.
        if (request.ResumeFrom > TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(Seconds(request.ResumeFrom));
        }

        // -re is an input option: read at the rate the file plays at, which is what the Java
        // version did by pacing the frames it decoded.
        arguments.Add("-thread_queue_size");
        arguments.Add("1024");
        arguments.Add("-readrate");
        arguments.Add("1");
        arguments.Add("-i");
        arguments.Add(request.InputPath);

        // JavaCV mapped the grabbed video/audio streams of the input file.
        arguments.Add("-map");
        arguments.Add("0:v:0");

        if (probe.HasAudio)
        {
            arguments.Add("-map");
            arguments.Add("0:a:0?");
        }

        AppendEncoderArguments(
            arguments, setting, frameRate, probe.HasAudio, probe.AudioChannels, ScaleFilter(setting));

        arguments.Add(request.OutputUrl);

        if (request.PreviewPath is { } previewPath)
        {
            // The scale of the setting applies to the output before it: the preview gets the same
            // picture the live does, then shrinks it on its own.
            var scale = ScaleFilter(setting);
            AppendPreviewOutput(arguments, "0:v:0", (scale is null ? string.Empty : scale + ",") + PreviewFilter, previewPath);
        }

        return arguments;
    }

    /// <summary>
    /// The canvas command: every source is its own input, the pictures are scaled into the
    /// rectangle they were dropped on and laid over each other, and the result is a single video
    /// and a single audio stream. This is what the previous version had no way to express: it had
    /// exactly one input, always a file.
    /// </summary>
    public static IReadOnlyList<string> BuildComposition(FfmpegCompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setting = request.Setting;
        var items = request.Items;

        // A canvas with nothing to see is not a live: a microphone is only heard, so at least one
        // source has to bring a picture onto the blank frame.
        var pictures = items
            .Select((item, index) => (item, index))
            .Where(entry => entry.item.Kind.HasPicture())
            .ToList();

        if (pictures.Count == 0)
        {
            throw new ArgumentException(
                "A composition needs at least one source with a picture", nameof(request));
        }

        var canvasWidth = Even(request.CanvasWidth);
        var canvasHeight = Even(request.CanvasHeight);
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new ArgumentException("The canvas has no usable size", nameof(request));
        }

        var frameRate = setting.FrameRate is > 0 ? setting.FrameRate.Value : request.CanvasFrameRate;
        if (frameRate <= 0)
        {
            frameRate = 25d;
        }

        var arguments = GlobalArguments();

        // Every input is opened before any filter is configured, which is the order ffmpeg needs.
        for (var index = 0; index < items.Count; index++)
        {
            AppendInput(arguments, items[index], request.ResumeFrom, frameRate);
        }

        // A label of the graph can feed one output only: with a preview the composed picture is
        // split in two, and the copy is shrunk inside the graph (-vf cannot act on a graph output).
        var graph = BuildFilterGraph(items, pictures, canvasWidth, canvasHeight, frameRate);
        var videoLabel = VideoLabel;
        if (request.PreviewPath is not null)
        {
            videoLabel = VideoLabel + "out";
            graph += $";[{VideoLabel}]split=2[{videoLabel}][{PreviewLabel}0];[{PreviewLabel}0]{PreviewFilter}[{PreviewLabel}]";
        }

        arguments.Add("-filter_complex");
        arguments.Add(Combine(graph, BuildAudioMix(items)));

        // A label of the graph is mapped in brackets: bare, ffmpeg reads it as an input index and
        // refuses the whole command line.
        arguments.Add("-map");
        arguments.Add($"[{videoLabel}]");

        if (HasAudioMix(items))
        {
            arguments.Add("-map");
            arguments.Add($"[{AudioLabel}]");
        }

        // The composed picture is already the size the canvas is, so the encoder is not asked to
        // scale again: -vf here would resize the result of the composition, not its base.
        AppendEncoderArguments(
            arguments, setting, frameRate, HasAudioMix(items), channels: 0, scaleFilter: null);

        arguments.Add(request.OutputUrl);

        if (request.PreviewPath is { } previewPath)
        {
            AppendPreviewOutput(arguments, $"[{PreviewLabel}]", filter: null, previewPath);
        }

        return arguments;
    }

    /// <summary>
    /// The second output of a live: one JPEG, overwritten a few times a second. Written to a
    /// temporary file and renamed, so the page never reads a frame that is half written.
    /// </summary>
    private static void AppendPreviewOutput(List<string> arguments, string map, string? filter, string path)
    {
        arguments.Add("-map");
        arguments.Add(map);
        arguments.Add("-an");
        arguments.Add("-sn");
        arguments.Add("-dn");
        if (filter is not null)
        {
            arguments.Add("-vf");
            arguments.Add(filter);
        }

        arguments.Add("-c:v");
        arguments.Add("mjpeg");
        // Quality 6 at this size: a quality a step lower costs more bytes than it buys, and a step
        // higher is the banding the small picture the page stretches it over would show.
        arguments.Add("-q:v");
        arguments.Add("6");
        arguments.Add("-f");
        arguments.Add("image2");
        arguments.Add("-update");
        arguments.Add("1");
        arguments.Add("-atomic_writing");
        arguments.Add("1");
        arguments.Add(path);
    }

    /// <summary>
    /// One frame of a source as a JPEG on stdout, for the tile the canvas draws before the live
    /// starts. The source is opened exactly the way the live will open it, so a device that cannot
    /// be snapshotted is a device that would not have streamed either.
    /// </summary>
    public static IReadOnlyList<string> BuildSnapshot(SourceKind kind, string target, int width)
    {
        if (!kind.HasPicture())
        {
            throw new ArgumentException("A microphone has no picture to snapshot", nameof(kind));
        }

        List<string> arguments = ["-hide_banner", "-nostdin", "-loglevel", "error"];

        // A file is looked at a second in, past the black frame most videos open on; the pacing
        // -re adds for a live would only make the one frame slower.
        var item = new FfmpegCompositionItem(kind, target, 0, 0, 0, 0, AudioEnabled: false);
        AppendInput(arguments, item, kind == SourceKind.File ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, frameRate: 5d);
        arguments.Remove("-re");

        arguments.AddRange(
        [
            "-frames:v", "1",
            "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={Even(width)}:-2"),
            "-f", "image2pipe",
            "-c:v", "mjpeg",
            "-q:v", "5",
            "pipe:1"
        ]);

        return arguments;
    }

    /// <summary>The options that are the same however the frames were produced.</summary>
    private static List<string> GlobalArguments() =>
    [
        "-hide_banner",
        "-nostdin",

        // A transcode that starts again with new parameters writes over what the previous one
        // sent: with a real ingest there is nothing to overwrite, and with a destination on
        // disk ffmpeg would otherwise stop to ask a question nobody is there to answer.
        "-y",

        "-loglevel",
        "error",

        // Where the transcode is, twice a second: that is the only place a running ffmpeg tells
        // how far it got, and a stop has to leave the position on the video row to resume there.
        "-progress",
        "pipe:1",
        "-nostats",
        "-stats_period",
        "0.2"
    ];

    private static void AppendInput(
        List<string> arguments,
        FfmpegCompositionItem item,
        TimeSpan resumeFrom,
        double frameRate)
    {
        // A capture device cannot be seeked into and does not need pacing: it produces frames when
        // there are frames to produce. A file does both, and carries on from where the interrupted
        // pass of this live stopped.
        if (item.Kind == SourceKind.File)
        {
            if (resumeFrom > TimeSpan.Zero)
            {
                arguments.Add("-ss");
                arguments.Add(Seconds(resumeFrom));
            }

            arguments.Add("-thread_queue_size");
            arguments.Add("1024");
            arguments.Add("-readrate");
            arguments.Add("1");
            arguments.Add("-i");
            arguments.Add(item.Target);
            return;
        }

        switch (item.Kind)
        {
            case SourceKind.Screen:
                // gdigrab reads the desktop and the monitors as they are; the frame rate it is told
                // to read them at is the only thing worth setting, everything else it decides.
                arguments.Add("-f");
                arguments.Add("gdigrab");
                arguments.Add("-framerate");
                arguments.Add(Number(frameRate));

                // gdigrab only knows the whole desktop: one monitor is the desktop cropped to the
                // rectangle that monitor occupies on it.
                if (MonitorTarget.TryParse(item.Target, out var left, out var top, out var width, out var height))
                {
                    arguments.Add("-offset_x");
                    arguments.Add(left.ToString(CultureInfo.InvariantCulture));
                    arguments.Add("-offset_y");
                    arguments.Add(top.ToString(CultureInfo.InvariantCulture));
                    arguments.Add("-video_size");
                    arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"));
                    arguments.Add("-i");
                    arguments.Add(MonitorTarget.Desktop);
                    break;
                }

                arguments.Add("-i");
                arguments.Add(item.Target);
                break;

            case SourceKind.Camera:
            case SourceKind.Microphone:
                arguments.Add("-f");
                arguments.Add("dshow");
                arguments.Add("-i");
                arguments.Add(item.Target);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Not a known source kind");
        }
    }

    /// <summary>
    /// The graph that turns N inputs into one picture. The canvas is a blank frame of the output
    /// size, and every source is scaled into the rectangle it was dropped on and laid over what is
    /// already there, in the order the user stacked them: no source is stretched to be the
    /// background, so an empty corner of the canvas stays empty on the live too.
    /// </summary>
    private static string BuildFilterGraph(
        IReadOnlyList<FfmpegCompositionItem> items,
        IReadOnlyList<(FfmpegCompositionItem Item, int Index)> pictures,
        int canvasWidth,
        int canvasHeight,
        double frameRate)
    {
        var graph = new StringBuilder();

        graph.Append(CultureInfo.InvariantCulture,
            $"color=c=black:s={canvasWidth}x{canvasHeight}:r={Number(frameRate)}[{CanvasLabel}];");

        // Every tile is scaled and normalised on its own, so the overlay only ever sees pictures
        // that already have the pixel format and the sample aspect the canvas wants. Its clock
        // starts at zero like the canvas does: a capture device stamps frames with the wall clock,
        // and an overlay would wait decades for the canvas to reach the first of them.
        var labels = new Dictionary<int, string>();
        foreach (var (item, index) in pictures)
        {
            var width = Even(item.Width);
            var height = Even(item.Height);
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException(
                    $"The source {item.Target} was dropped on a {item.Width}x{item.Height} rectangle",
                    nameof(items));
            }

            var label = $"tile{index}";
            graph.Append(CultureInfo.InvariantCulture,
                $"[{index}:v]setpts=PTS-STARTPTS,{Fit(width, height)},setsar=1[{label}];");
            labels[index] = label;
        }

        var composed = CanvasLabel;
        foreach (var (item, index) in pictures)
        {
            var next = $"stack{index}";
            graph.Append(CultureInfo.InvariantCulture,
                $"[{composed}][{labels[index]}]overlay={Even(item.X)}:{Even(item.Y)}:format=auto[{next}];");
            composed = next;
        }

        // The blank canvas has no clock of its own and would be drawn as fast as the encoder can
        // take it once the last file on it ends: realtime holds the output to the wall clock.
        graph.Append(CultureInfo.InvariantCulture, $"[{composed}]realtime[{VideoLabel}]");
        return graph.ToString();
    }

    /// <summary>
    /// The audio side of the same composition, as its own half of the graph. A source that was not
    /// asked for sound, or that cannot have any, is left out; more than one of them are mixed,
    /// because a live with two audio tracks where a platform expects one plays the first and
    /// ignores the rest.
    /// </summary>
    private static string? BuildAudioMix(IReadOnlyList<FfmpegCompositionItem> items)
    {
        var contributors = AudioContributors(items);
        if (contributors.Count == 0)
        {
            return null;
        }

        // The sound starts at zero for the same reason the tiles do: the picture does, and a
        // microphone stamped with the wall clock would never line up with it.
        var graph = new StringBuilder();
        foreach (var index in contributors)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[{index}:a]asetpts=PTS-STARTPTS[sound{index}];");
        }

        if (contributors.Count == 1)
        {
            // One contributor still needs the label the encoder maps, and anull gives it without
            // touching the sound.
            graph.Append(CultureInfo.InvariantCulture, $"[sound{contributors[0]}]anull[{AudioLabel}]");
            return graph.ToString();
        }

        foreach (var index in contributors)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[sound{index}]");
        }

        // dropout_transition keeps the mix from ducking every time a source has a silent moment,
        // which on a webcam is most of them.
        graph.Append(CultureInfo.InvariantCulture,
            $"amix=inputs={contributors.Count}:dropout_transition=0[{AudioLabel}]");
        return graph.ToString();
    }

    /// <summary>
    /// The inputs whose sound goes into the mix. gdigrab captures no sound at all, so a screen is
    /// never one of them whatever its tile says, and a camera opened as <c>video=…</c> alone has no
    /// audio stream either: asking for it fails the whole graph, which is why the sound of a webcam
    /// is a microphone source of its own. Whether a file really carries a track is ffmpeg's answer
    /// to give, not the canvas's.
    /// </summary>
    private static IReadOnlyList<int> AudioContributors(IReadOnlyList<FfmpegCompositionItem> items)
    {
        var contributors = new List<int>();
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].AudioEnabled && CanCarrySound(items[index]))
            {
                contributors.Add(index);
            }
        }

        return contributors;
    }

    private static bool CanCarrySound(FfmpegCompositionItem item) => item.Kind switch
    {
        SourceKind.Screen => false,
        SourceKind.Camera => item.Target.Contains("audio=", StringComparison.Ordinal),
        _ => true
    };

    private static bool HasAudioMix(IReadOnlyList<FfmpegCompositionItem> items) =>
        AudioContributors(items).Count > 0;

    /// <summary>Whether the composed live has a sound at all, once the silent sources are left out.</summary>
    public static bool CarriesSound(IReadOnlyList<FfmpegCompositionItem> items) => HasAudioMix(items);

    /// <summary>
    /// Video and sound are two halves of one <c>-filter_complex</c>: ffmpeg reads a single graph, so
    /// they are joined rather than passed as two filters.
    /// </summary>
    private static string Combine(string video, string? audio) =>
        audio is null ? video : $"{video};{audio}";

    /// <summary>
    /// Scale keeping the aspect ratio and padding what is left: a webcam dropped on a 16:9 tile
    /// arrives 4:3, and stretching it to fill is the one thing a user always notices.
    /// </summary>
    public static string Fit(int width, int height) =>
        $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";

    /// <summary>
    /// What the encoder will be asked to produce for a file: the resolution and the frame rate of
    /// the setting when it has one, the ones ffprobe read otherwise. This is the single place that
    /// decides it, so the command line and the numbers the preview shows can never disagree.
    /// </summary>
    public static MediaOutput ResolveOutput(VideoSettingEntity setting, MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(probe);

        return new MediaOutput(
            setting.VideoWidth is > 0 ? Even(setting.VideoWidth.Value) : probe.Width,
            setting.VideoHeight is > 0 ? Even(setting.VideoHeight.Value) : probe.Height,
            setting.FrameRate is > 0 ? setting.FrameRate.Value : probe.FrameRate > 0 ? probe.FrameRate : 25d);
    }

    /// <summary>
    /// The <c>scale</c> filter of a setting that asks for a resolution, or null when it keeps the
    /// one of the file. Both dimensions are rounded down to an even number: every pixel format a
    /// platform accepts (yuv420p above all) refuses an odd frame size, and a live that dies on the
    /// first frame of a new resolution is a much worse answer than one pixel of padding.
    /// </summary>
    public static string? ScaleFilter(VideoSettingEntity setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (setting.VideoWidth is not > 0 || setting.VideoHeight is not > 0)
        {
            return null;
        }

        // The name of the filter is part of the value: <c>-vf 160:120</c> asks ffmpeg for a filter
        // called "160:120", which is why a live that had never rescaled looked fine and the first
        // one that did died on the first frame instead of sending the smaller resolution.
        return $"scale={Even(setting.VideoWidth.Value)}:{Even(setting.VideoHeight.Value)}";
    }

    /// <summary>Port of the stream url composition of <c>StreamService#startLive</c>: the key is appended
    /// to the url, adding the separator only when needed.
    /// </summary>
    public static string BuildStreamingUrl(string streamUrl, string streamKey)
    {
        ArgumentNullException.ThrowIfNull(streamUrl);
        ArgumentNullException.ThrowIfNull(streamKey);

        return streamUrl.EndsWith('/') ? streamUrl + streamKey : streamUrl + "/" + streamKey;
    }

    /// <summary>
    /// The options that turn frames into the stream the platform expects. Shared by a live from a
    /// file and a live from a canvas, so the two are encoded the same way.
    /// </summary>
    private static void AppendEncoderArguments(
        List<string> arguments,
        VideoSettingEntity setting,
        double frameRate,
        bool hasAudio,
        int channels,
        string? scaleFilter)
    {
        if (!string.IsNullOrWhiteSpace(setting.VideoFormat))
        {
            arguments.Add("-f");
            arguments.Add(setting.VideoFormat.Trim());
        }

        arguments.Add("-c:v");
        arguments.Add(FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName));

        arguments.Add("-pix_fmt");
        arguments.Add(FfmpegCodecCatalog.ResolvePixelFormat(setting.PixelFormat));

        arguments.Add("-r");
        arguments.Add(Number(frameRate));

        // Only when the setting asks for a resolution of its own: without it the frame is
        // re-encoded exactly as the file is, which is what every stream did before the field.
        if (scaleFilter is not null)
        {
            arguments.Add("-vf");
            arguments.Add(scaleFilter);
        }

        if (setting.VideoBitrate is > 0)
        {
            arguments.Add("-b:v");
            arguments.Add(setting.VideoBitrate.Value.ToString(CultureInfo.InvariantCulture));
        }

        // Twitch strictly requires a keyframe every 2 seconds: gop = fps * gopSize.
        if (setting.GopSize is > 0)
        {
            arguments.Add("-g");
            arguments.Add(((int)(frameRate * setting.GopSize.Value)).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var option in setting.VideoSettingsOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Key))
            {
                continue;
            }

            arguments.Add($"-{option.Key!.Trim()}");
            if (option.Value is not null)
            {
                arguments.Add(option.Value);
            }
        }

        if (!hasAudio || setting.AudioSetting is null)
        {
            return;
        }

        arguments.Add("-c:a");
        arguments.Add(FfmpegCodecCatalog.ResolveAudioCodecName(setting.AudioSetting.AudioCodec));

        if (setting.AudioSetting.AudioBitrate is > 0)
        {
            arguments.Add("-b:a");
            arguments.Add(setting.AudioSetting.AudioBitrate.Value.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-ar");
        arguments.Add("44100");

        if (channels > 0)
        {
            arguments.Add("-ac");
            arguments.Add(channels.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int Even(int value) => value - (value % 2);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan position) =>
        Number(Math.Max(0d, position.TotalSeconds));
}
