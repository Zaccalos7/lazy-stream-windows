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
    string? PreviewPath = null,
    /// <summary>
    /// The platform the live goes to. With a relay the encoder writes FLV on its standard output
    /// for the pacer instead of reaching the ingest itself (see <see cref="FlvPacedRelay"/>).
    /// Null is the single ffmpeg of before.
    /// </summary>
    StreamPlatformProfile? Profile = null,
    /// <summary>
    /// The encoder quality the live goes out at, already decided for this machine when the setting
    /// says automatic (see EncoderTuningService). Null reads it off the setting.
    /// </summary>
    EncoderQuality? Quality = null);

/// <summary>One source on the canvas, as the command line needs it.</summary>
public sealed record FfmpegCompositionItem(
    SourceKind Kind,
    string Target,
    int X,
    int Y,
    int Width,
    int Height,
    bool AudioEnabled,
    /// <summary>A file the GPU was measured to decode faster than the CPU (see MediaProxyService).</summary>
    bool HardwareDecoding = false,
    /// <summary>How loud the source is in the mix, in percent: 100 leaves its sound as it is.</summary>
    int Volume = 100);

/// <summary>
/// What makes a light copy (see <see cref="FfmpegCommandBuilder.BuildProxy"/>): the GPU decoder or
/// the CPU, and a GPU encoder (h264_nvenc, h264_qsv, h264_amf) or x264.
/// </summary>
public sealed record ProxyEncoding(bool HardwareDecoding, string? Encoder)
{
    public static readonly ProxyEncoding Cpu = new(false, null);

    public override string ToString() =>
        (HardwareDecoding ? "GPU decoding" : "CPU decoding") + ", " + (Encoder ?? "x264");
}

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
    /// <summary>
    /// How long the whole canvas streams, when it has an end. A canvas of nothing but files has
    /// one, and it is the length of the longest of them: a video that runs out while the others
    /// are still going leaves its shape on the canvas (the overlay holds its last frame) and the
    /// live goes on, exactly as a playlist goes on to the next video. When the longest one is over
    /// the live is over, which is what <c>-t</c> is for: ffmpeg stops on its own instead of
    /// waiting to be asked, so the connection to the platform is closed by the process that opened
    /// it.
    /// <para>Null for a canvas with a capture device on it: a device has no end to reach.</para>
    /// </summary>
    TimeSpan? Duration = null,
    string? PreviewPath = null,
    StreamPlatformProfile? Profile = null,
    EncoderQuality? Quality = null);

/// <summary>
/// Translates the <c>FFmpegFrameRecorder</c> configuration of <c>StreamService</c> into the
/// equivalent ffmpeg command line. The Java version decoded every frame in Java and re-encoded
/// it with real time pacing. For a known platform that pacing is back, on the encoded frames, in
/// <see cref="FlvPacedRelay"/>; for any other destination <c>-re</c> gives it to the ffmpeg process.
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
    /// The preview is a picture to look at, not a second live: thirty frames a second, small, as
    /// JPEG. It costs next to nothing next to the live encode, and it is what is on air (the
    /// composed canvas, the scaled file) rather than the source the page would otherwise replay.
    ///
    /// The size is what makes it read well: at 426 pixels wide the frame was stretched over a stage
    /// twice as wide, and a blurred mosaic moving in steps is worse to watch than a sharp picture
    /// moving smoothly. 640 is as wide as the stage, so nothing is scaled up.
    ///
    /// Thirty frames a second is not decoration. Half of that rate is fifteen beats a second of
    /// which every other one is two beats long, and a picture that alternates between 33 and 66
    /// milliseconds is a picture that judders however carefully it is fetched: the page cannot draw
    /// a frame that was never written, and there is no page that can make fifteen of them a
    /// second look like motion. A bigger preview would be sharper still, and it would be taken out
    /// of the live encode, which has to finish its own frame in the time it has, so 640 is where it
    /// stops: encoding a frame this size costs a couple of milliseconds and the preview stays a
    /// small slice of a core next to the live.
    /// </summary>
    private const string PreviewFilter = "fps=30,scale=w='min(640,iw)':h=-2";

    /// <summary>
    /// The real-time buffer of a dshow device: a couple of seconds of raw 1080p, enough to ride out
    /// the start of the live and a slow moment of the encoder (see <see cref="AppendInput"/>).
    /// </summary>
    private const string DeviceBufferSize = "256M";

    public static IReadOnlyList<string> Build(FfmpegStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setting = request.Setting;
        var probe = request.Probe;

        // The rate the encoder is asked for: what the setting asks for, otherwise the rate ffprobe
        // read from the file (25 is the fallback of the probe, kept for a probe that knows nothing).
        var output = ResolveOutput(setting, probe);
        var frameRate = output.FrameRate;
        var profile = request.Profile;
        var relay = profile is { UsesRelay: true };

        var arguments = GlobalArguments(relay);

        // Where to carry on from, before the input: as an input option ffmpeg seeks there and starts
        // reading immediately, where an output option would decode and throw the frames away.
        if (request.ResumeFrom > TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(Seconds(request.ResumeFrom));
        }

        // -re is an input option: read at the rate the file plays at, which is what the Java
        // version did by pacing the frames it decoded. With a relay the pacer is that clock, on
        // the encoded frames, and the encoder runs as far ahead as the relay lets it.
        arguments.Add("-thread_queue_size");
        arguments.Add("512");
        if (!relay)
        {
            arguments.Add("-readrate");
            arguments.Add("1");
        }

        arguments.Add("-i");
        arguments.Add(request.InputPath);

        var silence = NeedsSilence(profile, probe.HasAudio);
        if (silence)
        {
            AppendSilenceInput(arguments, profile!);
        }

        // JavaCV mapped the grabbed video/audio streams of the input file.
        arguments.Add("-map");
        arguments.Add("0:v:0");

        if (probe.HasAudio)
        {
            arguments.Add("-map");

            // The `?` that makes a map optional is what keeps ffmpeg alive when the stream it names
            // is not there, and on a platform that needs a sound that is the worst thing it could do:
            // the live would go out with a picture and no sound, the ingest would accept the publish
            // as it always does, and nothing would ever be broadcast - a live that reads as
            // connected and is not there, with nothing in the log to say why. Where the platform
            // needs the sound the map is mandatory, so ffmpeg stops on the first frame with its own
            // message about the track, which is a mistake the user can act on. Everywhere else the
            // optional map is kept: a live with no sound is a normal thing on Twitch.
            arguments.Add(profile is { RequiresAudio: true } ? "0:a:0" : "0:a:0?");
        }
        else if (silence)
        {
            arguments.Add("-map");
            arguments.Add("1:a:0");
        }

        AppendEncoderArguments(
            arguments,
            setting,
            frameRate,
            probe.HasAudio || silence,
            silence ? 2 : probe.AudioChannels,
            ScaleFilter(setting),
            profile,
            request.Quality);

        // The silent track never ends: the picture decides when the live does.
        if (silence)
        {
            arguments.Add("-shortest");
        }

        AppendDestination(arguments, request.OutputUrl, relay);

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

        var profile = request.Profile;
        var relay = profile is { UsesRelay: true };
        var arguments = GlobalArguments(relay);

        // Who keeps the time of the canvas. A capture device does, whenever there is one: it
        // produces frames in real time, and the files are read at their own rate to stay in step
        // with it.
        //
        // A canvas of nothing but files has no such source, and a -readrate on every file is one
        // clock per file: each input measures its lag against its own start, and once the encoder
        // or the relay holds them back, each wins it back on its own at 1.05x (the default of
        // -readrate_catchup). The overlay keeps the pictures in step by their timestamps, so the
        // canvas moves at the pace of whichever file is furthest behind, slower than real time for
        // as long as it is behind - two videos on a scene are a live in slow motion. So the canvas
        // gets one clock, on what comes out: the relay when there is one, exactly as a single file
        // does, otherwise the realtime filter at the end of the graph. The files are decoded as
        // fast as that clock asks, and the overlay lines them up frame by frame.
        var onlyFiles = items.All(item => item.Kind == SourceKind.File);
        var pacedInputs = !onlyFiles;
        var pacedGraph = !(onlyFiles && relay);

        // Every input is opened before any filter is configured, which is the order ffmpeg needs.
        for (var index = 0; index < items.Count; index++)
        {
            AppendInput(arguments, items[index], request.ResumeFrom, frameRate, pacedInputs);
        }

        // The silent track is the input after the last source, so the indexes of the graph stay put.
        var silence = NeedsSilence(profile, HasAudioMix(items));
        if (silence)
        {
            AppendSilenceInput(arguments, profile!);
        }

        // A label of the graph can feed one output only: with a preview the composed picture is
        // split in two, and the copy is shrunk inside the graph (-vf cannot act on a graph output).
        var graph = BuildFilterGraph(items, pictures, canvasWidth, canvasHeight, frameRate, pacedGraph);
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
        else if (silence)
        {
            arguments.Add("-map");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{items.Count}:a:0"));
        }

        // The composed picture is already the size the canvas is, so the encoder is not asked to
        // scale again: -vf here would resize the result of the composition, not its base.
        AppendEncoderArguments(
            arguments, setting, frameRate, HasAudioMix(items) || silence, channels: silence ? 2 : 0, scaleFilter: null, profile, request.Quality);

        // A canvas of devices has no end and neither has the silent track: -shortest only matters
        // for a canvas of files, whose picture ends when its longest file does.
        if (silence)
        {
            arguments.Add("-shortest");
        }

        // The length of the canvas is an output option: it counts what goes on air, whatever the
        // inputs do. It stands before the destination, which is where every output option goes.
        // A canvas started again from where it was interrupted reads from that point on, so the
        // clock of the output carries the part already streamed: the live lasts the same either way.
        if (request.Duration is { } duration && duration > TimeSpan.Zero)
        {
            arguments.Add("-t");
            arguments.Add(Seconds(duration + request.ResumeFrom));
        }

        AppendDestination(arguments, request.OutputUrl, relay);

        if (request.PreviewPath is { } previewPath)
        {
            AppendPreviewOutput(arguments, $"[{PreviewLabel}]", filter: null, previewPath);
        }

        return arguments;
    }

    /// <summary>
    /// The second output of a live: one JPEG, overwritten a few times a second.
    ///
    /// It is written straight over the file it was writing last, never beside it and renamed on
    /// top. ffmpeg can do that swap (-atomic_writing), and on Windows it fails: renaming over a file
    /// is refused with "Operation not permitted" as soon as anything holds that file open for a
    /// moment, and a scanner that reads every frame the preview writes holds it for a moment of
    /// every frame. The preview then never moves - the frame lands in the .tmp file thirty times a
    /// second and the rename in front of the page fails thirty times a second instead, which is the
    /// "failed to rename file" a live used to end its error message with, repeated thousands of
    /// times.
    ///
    /// What the rename was there to buy - the page is never handed half a picture - is bought on the
    /// reading side instead (LivePreviewFrames): the file ffmpeg overwrites is shortened before it
    /// is written, so a read that catches it halfway holds the front of the frame and no end, and
    /// that is a read made again rather than a picture sent.
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

        // One thread, and this is the whole of the isolation between the preview and the live. The
        // two encoders are one process, so a preview left free to take the threads it wants takes
        // them from the encode that is being sent to the platform - and the platform, not this
        // page, is the one that decides the live is good. A 640 pixel JPEG takes a couple of
        // milliseconds to encode, so one thread never becomes the reason the preview misses a beat,
        // while the live keeps every thread its own codec asked for.
        arguments.Add("-threads");
        arguments.Add("1");

        arguments.Add("-f");
        arguments.Add("image2");

        // One file, overwritten: the page asks for the newest picture and is answered with a whole
        // one or with nothing at all, never with a file that is on its way to being the next frame.
        arguments.Add("-update");
        arguments.Add("1");
        arguments.Add(path);
    }

    /// <summary>
    /// The light copy of a heavy file a canvas streams instead of it (see MediaProxyService): no
    /// bigger than <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, never scaled up, 8 bit
    /// H.264 and AAC, on the same timeline as the file so a live resumes in it at the same second.
    /// <para>The scale comes first, so whatever follows works on a fraction of the pixels. An HDR
    /// file is tone mapped to SDR after it: dropping ten bits of PQ to eight without it is the
    /// grey, washed out picture a live of an HDR file otherwise has.</para>
    /// <para><paramref name="encoding"/> says what does the work: the CPU, the GPU decoder, a GPU
    /// encoder. The filters stay on the CPU either way, so a decoded frame comes back from the GPU
    /// before it is scaled, and that copy is why the GPU is not always the faster one. A
    /// <paramref name="trial"/> runs the same command on the first seconds of the file into
    /// nothing, which is how the faster one is found.</para>
    /// <para>The progress goes to stdout, for the log to say how far a long file got.</para>
    /// </summary>
    public static IReadOnlyList<string> BuildProxy(
        string inputPath,
        string outputPath,
        int maxWidth,
        int maxHeight,
        bool toneMap,
        ProxyEncoding? encoding = null,
        TimeSpan? trial = null)
    {
        encoding ??= ProxyEncoding.Cpu;

        var filter = string.Create(CultureInfo.InvariantCulture,
            $"scale=w='min({maxWidth},iw)':h='min({maxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2");
        if (toneMap)
        {
            filter += ",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv";
        }

        filter += ",format=yuv420p";

        List<string> arguments =
            ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1"];

        if (encoding.HardwareDecoding)
        {
            // Whatever decoder the machine has (d3d11va, dxva2, cuda, qsv, vaapi), and the CPU when
            // none of them opens: auto never fails a command for want of a GPU.
            arguments.AddRange(["-hwaccel", "auto"]);
        }

        arguments.AddRange(["-i", inputPath, "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter]);
        arguments.AddRange(ProxyVideoEncoder(encoding.Encoder));

        if (toneMap)
        {
            arguments.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }

        arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);

        if (trial is { } seconds)
        {
            arguments.AddRange(["-t", Seconds(seconds), "-f", "null", "-"]);
            return arguments;
        }

        // The output is written under a name that is not the proxy's until it is whole, so the
        // container is named rather than read off the extension.
        arguments.AddRange(["-f", "mp4", outputPath]);
        return arguments;
    }

    /// <summary>
    /// The quality a copy is made at, as each encoder spells it: about what CRF 20 is to x264, which
    /// is more than the live will keep once it encodes it again at its own bitrate.
    /// </summary>
    private static string[] ProxyVideoEncoder(string? encoder) => encoder switch
    {
        null => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20"],
        "h264_nvenc" => ["-c:v", encoder, "-preset", "p4", "-rc", "vbr", "-cq", "21", "-b:v", "0"],
        "h264_qsv" => ["-c:v", encoder, "-preset", "veryfast", "-global_quality", "21"],
        "h264_amf" => ["-c:v", encoder, "-quality", "speed", "-rc", "cqp", "-qp_i", "20", "-qp_p", "22"],
        _ => ["-c:v", encoder]
    };

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
        // -readrate adds for a live would only make the one frame slower.
        var item = new FfmpegCompositionItem(kind, target, 0, 0, 0, 0, AudioEnabled: false);
        AppendInput(arguments, item, kind == SourceKind.File ? TimeSpan.FromSeconds(1) : TimeSpan.Zero, frameRate: 5d, paced: false);

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

    /// <summary>
    /// The options that are the same however the frames were produced. With a relay the standard
    /// output carries the stream, so the progress is read off the sender instead: it is the one
    /// that knows what reached the ingest.
    /// </summary>
    private static List<string> GlobalArguments(bool relay = false)
    {
        var threadCount = Math.Max(1, Environment.ProcessorCount / 2);
        if (relay)
        {
            return
            [
                "-hide_banner",
                "-nostdin",
                "-y",
                "-loglevel",
                "error",
                "-threads",
                threadCount.ToString(CultureInfo.InvariantCulture),
                "-nostats"
            ];
        }

        return
        [
            "-hide_banner",
            "-nostdin",

            // A transcode that starts again with new parameters writes over what the previous one
            // sent: with a real ingest there is nothing to overwrite, and with a destination on
            // disk ffmpeg would otherwise stop to ask a question nobody is there to answer.
            "-y",

            "-loglevel",
            "error",

            // Limit threads to half CPU cores to leave headroom for OS and .NET runtime
            "-threads",
            threadCount.ToString(CultureInfo.InvariantCulture),

            // Where the transcode is, twice a second: that is the only place a running ffmpeg tells
            // how far it got, and a stop has to leave the position on the video row to resume there.
            "-progress",
            "pipe:1",
            "-nostats",
            "-stats_period",
            "0.2"
        ];
    }

    private static void AppendInput(
        List<string> arguments,
        FfmpegCompositionItem item,
        TimeSpan resumeFrom,
        double frameRate,
        bool paced)
    {
        // A capture device cannot be seeked into and does not need pacing: it produces frames when
        // there are frames to produce. A file does both, and carries on from where the interrupted
        // pass of this live stopped; it is paced only when a device sets the time of the canvas.
        if (item.Kind == SourceKind.File)
        {
            if (resumeFrom > TimeSpan.Zero)
            {
                arguments.Add("-ss");
                arguments.Add(Seconds(resumeFrom));
            }

            arguments.Add("-thread_queue_size");
            arguments.Add("512");
            if (item.HardwareDecoding)
            {
                arguments.Add("-hwaccel");
                arguments.Add("auto");
            }

            if (paced)
            {
                arguments.Add("-readrate");
                arguments.Add("1");
            }

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

                // dshow pushes raw frames into a buffer of its own whether or not ffmpeg reads them,
                // and the default (3 MB) holds less than one 1080p frame of a webcam. Every stall
                // fills it - above all the seconds the RTMP handshake takes before the first frame
                // is read - and from there every frame is dropped. The size is a ceiling, not an
                // allocation: memory is only taken while the reader is behind.
                arguments.Add("-rtbufsize");
                arguments.Add(DeviceBufferSize);
                arguments.Add("-thread_queue_size");
                arguments.Add("1024");
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
        double frameRate,
        bool paced)
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
            // For layout compositions, respect layout dimensions, scaling to fill and cropping as needed
            graph.Append(CultureInfo.InvariantCulture,
                $"[{index}:v]setpts=PTS-STARTPTS,{Fit(width, height, true)},setsar=1[{label}];");
            labels[index] = label;
        }

        var composed = CanvasLabel;
        foreach (var (item, index) in pictures)
        {
            var next = $"stack{index}";
            var xExpr = $"{Even(item.X)}+({Even(item.Width)}-w)/2";
            var yExpr = $"{Even(item.Y)}+({Even(item.Height)}-h)/2";
            graph.Append(CultureInfo.InvariantCulture,
                $"[{composed}][{labels[index]}]overlay={xExpr}:{yExpr}:format=auto[{next}];");
            composed = next;
        }

        // The blank canvas has no clock of its own and would be drawn as fast as the encoder can
        // take it once the last file on it ends: realtime holds the output to the wall clock.
        // Behind a relay that clock is the relay's, and a second one here would only keep the
        // encoder from running ahead into the jitter buffer (and the preroll from ever going out).
        var clock = paced ? "realtime" : "null";
        graph.Append(CultureInfo.InvariantCulture, $"[{composed}]{clock}[{VideoLabel}]");
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
            // The level of each source is set before the mix, so one loud video can be brought down
            // to the others without touching them. 100 is the sound as it is and adds no filter.
            var volume = Math.Max(0, items[index].Volume);
            var level = volume == 100
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $",volume={Number(volume / 100d)}");
            graph.Append(CultureInfo.InvariantCulture, $"[{index}:a]asetpts=PTS-STARTPTS{level}[sound{index}];");
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
    /// Scale keeping the aspect ratio: a webcam dropped on a 16:9 tile arrives 4:3, and
    /// stretching it to fill is the one thing a user always notices. The scaled frame is then
    /// placed in the center of the tile area by the overlay filter rather than padded.
    /// </summary>
    public static string Fit(int width, int height, bool fill = false)
    {
        if (fill)
        {
            return $"scale={width}:{height}:force_original_aspect_ratio=increase:force_divisible_by=2,crop={width}:{height}";
        }
        return $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
    }

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

        // A key is copied from the dashboard of the platform and pasted, and a space or a line
        // break comes along more often than not. The ingest takes the publish all the same - the
        // key is checked after it - so a key with a space on the end is a live that connects, is
        // accepted, and never appears on the channel. Neither a key nor an address has a space
        // that means anything.
        streamUrl = streamUrl.Trim();
        streamKey = streamKey.Trim();
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
        string? scaleFilter,
        StreamPlatformProfile? profile = null,
        EncoderQuality? quality = null)
    {
        // The relay reads FLV whatever the setting names: it is the container of RTMP, and the
        // only one whose tags carry the timestamp the pacer needs.
        var format = profile is { UsesRelay: true } ? "flv" : setting.VideoFormat?.Trim();
        if (!string.IsNullOrWhiteSpace(format))
        {
            arguments.Add("-f");
            arguments.Add(format);
        }

        var codecName = FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName);
        arguments.Add("-c:v");
        arguments.Add(codecName);

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
            var bitrate = setting.VideoBitrate.Value.ToString(CultureInfo.InvariantCulture);
            arguments.Add("-b:v");
            arguments.Add(bitrate);
            // Constrain rate for CBR-like streaming: maxrate = bitrate, bufsize = 2x bitrate
            arguments.Add("-maxrate");
            arguments.Add(bitrate);
            arguments.Add("-bufsize");
            arguments.Add((setting.VideoBitrate.Value * 2).ToString(CultureInfo.InvariantCulture));
        }

        // Constant frame rate output for stable streaming. -fps_mode is the name -vsync has had
        // since ffmpeg 5.1; the old one is deprecated and only warns, for now.
        arguments.Add("-fps_mode");
        arguments.Add("cfr");

        // Twitch strictly requires a keyframe every 2 seconds: gop = fps * gopSize.
        if (setting.GopSize is > 0)
        {
            arguments.Add("-g");
            arguments.Add(((int)(frameRate * setting.GopSize.Value)).ToString(CultureInfo.InvariantCulture));
        }

        // -g counts frames, and a frame count is only a duration at the rate it was computed
        // with: a scene cut or a rate that is not a whole number moves the keyframes off the
        // two seconds the platforms check. Forcing them on the clock pins them there.
        var keyframeSeconds = setting.GopSize is > 0 ? setting.GopSize.Value : profile?.KeyframeSeconds ?? 0;
        if (profile is { KeyframeSeconds: > 0 } && keyframeSeconds > 0
            && !setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "force_key_frames"))
        {
            arguments.Add("-force_key_frames");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"expr:gte(t,n_forced*{Number(keyframeSeconds)})"));
        }

        // x264-specific low-CPU options (applied when user hasn't overridden via VideoSettingsOptions)
        var isLibX264 = codecName.Equals("libx264", StringComparison.OrdinalIgnoreCase);
        var hasPreset = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "preset");
        var hasTune = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "tune");
        var hasProfile = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "profile");

        // The switch of the settings form. Off is the sane choice for a re-stream, where nothing
        // here reaches a viewer in less than the seconds the relay and the ingest already spend on
        // the way, and where an encoder that cannot look ahead starves itself after every keyframe.
        // A canvas of two or more pictures turns it back on for itself (VideoSettingLatency).
        var fastEncoder = VideoSettingLatency.IsOn(setting);
        
        // The level of the setting, spelled the way this encoder spells it. A preset typed into the
        // options by hand is more specific than a level, so it wins.
        var level = quality ?? Concrete(VideoSettingQuality.Of(setting));
        if (!hasPreset)
        {
            arguments.AddRange(PresetArguments(codecName, level));
        }

        if (isLibX264)
        {
            if (!hasTune && fastEncoder)
            {
                arguments.Add("-tune");
                arguments.Add("zerolatency");
            }
            // High is what every platform takes, and its 8x8 transform is detail main cannot keep
            // at the same bitrate.
            if (!hasProfile)
            {
                arguments.Add("-profile:v");
                arguments.Add("high");
            }
            // Reduce CPU further: disable scenecut and lookahead. The lookahead is what makes the
            // encoder able to see a forced keyframe coming, so without it the rate control buffer
            // fills on the keyframe and the frames after it have nothing left to spend: that is a
            // stall of the encoder, which the ingest reads as the live going quiet.
            var hasX264Params = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "x264-params");
            if (!hasX264Params && fastEncoder)
            {
                arguments.Add("-x264-params");
                arguments.Add("scenecut=0:rc_lookahead=0");
            }
        }

        // Hardware encoder low-latency defaults (when user hasn't overridden)
        var isNvenc = codecName.Contains("_nvenc", StringComparison.OrdinalIgnoreCase);
        var isQsv = codecName.Contains("_qsv", StringComparison.OrdinalIgnoreCase);
        var isAmf = codecName.Contains("_amf", StringComparison.OrdinalIgnoreCase);
        
        if (isNvenc || isQsv || isAmf)
        {
            // hasPreset and hasTune are the ones read above for x264: the same keys, the same answer.
            var hasRc = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "rc");
            var hasCq = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "cq");

            if (!hasTune && isNvenc && fastEncoder)
            {
                arguments.Add("-tune");
                arguments.Add("ll");  // NVENC low latency
            }
            if (!hasRc)
            {
                arguments.Add("-rc");
                arguments.Add("cbr");  // Constant bitrate for streaming
            }
            if (!hasCq)
            {
                arguments.Add("-cq");
                arguments.Add("23");   // Quality level for CQP modes
            }
            // NVENC: zero latency mode
            if (isNvenc && fastEncoder)
            {
                var hasDelay = setting.VideoSettingsOptions.Any(o => o.Key?.Trim() == "delay");
                if (!hasDelay)
                {
                    arguments.Add("-delay");
                    arguments.Add("0");
                }
            }
        }

        foreach (var option in setting.VideoSettingsOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Key))
            {
                continue;
            }

            var key = option.Key!.Trim();

            // A decision of this application, read above: ffmpeg has no such option and would stop
            // on it before the first frame.
            if (VideoSettingQuality.InternalKeys.Contains(key))
            {
                continue;
            }

            arguments.Add($"-{key}");
            if (option.Value is not null)
            {
                arguments.Add(isLibX264 && key == "preset" ? CleanPreset(option.Value) : option.Value);
            }
        }

        if (!hasAudio)
        {
            return;
        }

        // A platform that requires sound gets AAC even from a setting that names no audio: a
        // track that is mapped and not encoded is a command ffmpeg refuses.
        var audio = setting.AudioSetting;
        if (audio is null && profile is not { RequiresAudio: true })
        {
            return;
        }

        arguments.Add("-c:a");
        arguments.Add(audio is null ? "aac" : FfmpegCodecCatalog.ResolveAudioCodecName(audio.AudioCodec));

        var audioBitrate = audio?.AudioBitrate is > 0 ? audio.AudioBitrate.Value : audio is null ? 128_000 : 0;
        if (audioBitrate > 0)
        {
            arguments.Add("-b:a");
            arguments.Add(audioBitrate.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("-ar");
        arguments.Add((profile?.AudioSampleRate ?? 44_100).ToString(CultureInfo.InvariantCulture));

        if (channels > 0)
        {
            arguments.Add("-ac");
            arguments.Add(channels.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The preset of every level for the encoders that have one, and nothing for the others.
    /// <para>x264: superfast is the lightest preset that keeps the deblocking filter and adaptive
    /// quantisation (ultrafast drops both, which is the mosaic on screen); veryfast is what the
    /// streaming tools default to; faster is the step up for a CPU with room to spare.</para>
    /// <para>NVENC, QSV and AMF encode on a block of the GPU made for it: a higher level costs that
    /// block time per frame, not CPU, so it is only worth it on a GPU that still keeps up with it,
    /// which is what <see cref="EncoderQuality.Auto"/> measures.</para>
    /// </summary>
    public static IReadOnlyList<string> PresetArguments(string codecName, EncoderQuality quality)
    {
        var level = Concrete(quality);
        var name = codecName.Trim().ToLowerInvariant();
        string? preset = name switch
        {
            "libx264" or "libx265" => level switch
            {
                EncoderQuality.Light => "superfast",
                EncoderQuality.High => "faster",
                _ => "veryfast"
            },
            _ when name.EndsWith("_nvenc", StringComparison.Ordinal) => level switch
            {
                EncoderQuality.Light => "p1",
                EncoderQuality.High => "p6",
                _ => "p4"
            },
            _ when name.EndsWith("_qsv", StringComparison.Ordinal) => level switch
            {
                EncoderQuality.Light => "veryfast",
                EncoderQuality.High => "slower",
                _ => "medium"
            },
            _ => null
        };

        if (preset is not null)
        {
            return ["-preset", preset];
        }

        // AMF names its levels quality, and calls them speed, balanced and quality.
        if (name.EndsWith("_amf", StringComparison.Ordinal))
        {
            return ["-quality", level switch
            {
                EncoderQuality.Light => "speed",
                EncoderQuality.High => "quality",
                _ => "balanced"
            }];
        }

        return [];
    }

    /// <summary>A level that is still automatic once nothing measured it is the balanced one.</summary>
    private static EncoderQuality Concrete(EncoderQuality quality) =>
        quality == EncoderQuality.Auto ? EncoderQuality.Balanced : quality;

    /// <summary>
    /// A few seconds of a synthetic picture at the size and rate of the live, encoded into nothing
    /// with the encoder and the level being considered: how long it takes, against how long it
    /// lasts, is how much headroom this machine has at that level. The picture moves and is full
    /// of detail, so it costs the encoder what a real one does.
    /// </summary>
    public static IReadOnlyList<string> BuildEncoderTrial(
        string codecName, EncoderQuality quality, int width, int height, double frameRate, int bitrate, TimeSpan duration)
    {
        List<string> arguments =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={Even(width)}x{Even(height)}:rate={Number(frameRate)}"),
            "-t", Seconds(duration),
            "-threads", Math.Max(1, Environment.ProcessorCount / 2).ToString(CultureInfo.InvariantCulture),
            "-c:v", codecName,
            "-pix_fmt", "yuv420p"
        ];
        arguments.AddRange(PresetArguments(codecName, quality));
        arguments.AddRange(["-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-f", "null", "-"]);
        return arguments;
    }

    /// <summary>
    /// ultrafast is the one x264 preset that turns the deblocking filter and adaptive quantisation
    /// off, and those two are what keep a frame from breaking into blocks: at a streaming bitrate
    /// it is the mosaic a viewer sees on every movement. superfast keeps both for very little more
    /// CPU, so a setting that asks for the lightest encode gets the lightest clean one.
    /// </summary>
    internal static string CleanPreset(string preset) =>
        preset.Trim().Equals("ultrafast", StringComparison.OrdinalIgnoreCase) ? "superfast" : preset;

    /// <summary>
    /// Where the encoded stream goes: the ingest itself, or, with a relay, the standard output in
    /// FLV for the pacer. Every packet is flushed as soon as it is muxed, so the pacer sees each
    /// frame when it exists instead of in 32 KB lumps; and the muxer is told not to go back to the
    /// start to write a duration, which a pipe cannot do.
    /// </summary>
    private static void AppendDestination(List<string> arguments, string outputUrl, bool relay)
    {
        if (!relay)
        {
            arguments.Add(outputUrl);
            return;
        }

        arguments.Add("-flvflags");
        arguments.Add("no_duration_filesize");
        arguments.Add("-flush_packets");
        arguments.Add("1");
        arguments.Add("pipe:1");
    }

    /// <summary>
    /// The second ffmpeg of a relay, for <see cref="RelayTransport.FfmpegSender"/> only - the
    /// fallback of the RTMP that .NET speaks itself (<see cref="Rtmp.RtmpPublisher"/>). It reads
    /// the paced FLV on its standard input and copies it to the ingest without touching a frame. It is the process that
    /// talks to the platform, so it is also the one whose progress says what reached it: the
    /// position of the live and the proof that the ingest is taking the stream are both read here.
    /// </summary>
    public static IReadOnlyList<string> BuildSender(string outputUrl, StreamPlatformProfile profile)
    {
        ArgumentNullException.ThrowIfNull(outputUrl);
        ArgumentNullException.ThrowIfNull(profile);

        return
        [
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-progress",
            "pipe:1",
            "-nostats",
            "-stats_period",
            "0.2",

            // The stream is the encoder's own output: nothing to discover in it but the two codecs,
            // which the FLV header and its first tags already name. A long probe would only hold
            // the connection back by that much.
            "-probesize",
            "65536",
            "-analyzeduration",
            "500000",
            "-f",
            "flv",
            "-i",
            "pipe:0",
            "-map",
            "0",
            "-c",
            "copy",
            "-f",
            "flv",
            "-flvflags",
            "no_duration_filesize",

            // A connection that hangs - an ingest that never answers, a network that stopped
            // taking data - fails after this long instead of looking like a live for ever.
            "-rw_timeout",
            ((long)profile.ConnectTimeout.TotalMilliseconds * 1000).ToString(CultureInfo.InvariantCulture),
            outputUrl
        ];
    }

    /// <summary>Whether the platform needs a sound the source does not have.</summary>
    private static bool NeedsSilence(StreamPlatformProfile? profile, bool hasAudio) =>
        profile is { RequiresAudio: true } && !hasAudio;

    /// <summary>
    /// A silent stereo track at the rate of the platform. YouTube keeps a live with no audio
    /// stream off air - the connection is accepted, the broadcast never starts - and silence is
    /// the cheapest audio there is.
    /// </summary>
    private static void AppendSilenceInput(List<string> arguments, StreamPlatformProfile profile)
    {
        arguments.Add("-f");
        arguments.Add("lavfi");
        arguments.Add("-i");
        arguments.Add(string.Create(
            CultureInfo.InvariantCulture, $"anullsrc=channel_layout=stereo:sample_rate={profile.AudioSampleRate}"));
    }

    private static int Even(int value) => value - (value % 2);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan position) =>
        Number(Math.Max(0d, position.TotalSeconds));
}
