using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>The video side of a live, either as the file carries it or as the encoder produces it.</summary>
public sealed record LiveMedia(int Width, int Height, double FrameRate, bool HasAudio, int AudioChannels);

/// <summary>
/// The encoder configuration of a running live. It carries the ffmpeg names next to the stored
/// ids, so the page can label every row and fill every control without probing anything.
/// </summary>
public sealed record LiveParameters(
    int? VideoCodec,
    string? VideoCodecLabel,
    string? VideoCodecName,
    int? PixelFormat,
    string? PixelFormatName,
    int? VideoBitrate,
    int? GopSize,
    int? AudioCodec,
    string? AudioCodecLabel,
    int? AudioBitrate,
    int? VideoWidth,
    int? VideoHeight,
    double? FrameRate);

/// <summary>
/// A source of a canvas whose sound goes into the mix of the live, with how loud it is in it
/// (percent, 100 being the sound as the source has it).
/// </summary>
public sealed record LiveSource(int Pkid, string Name, string Kind, int Volume);

/// <summary>One of the running lives, for the picker when more than one channel is on air.</summary>
public sealed record LiveOption(int VideoPkid, string ChannelName, string VideoName, bool IsWatching);

/// <summary>
/// Everything the preview page draws, sampled once a second. The fields that only mean something
/// while something is running keep their default when nothing is, and <see cref="IsLive"/> is what
/// tells the two apart.
/// </summary>
public sealed record LiveSnapshot(
    bool IsLive,
    bool Reconfiguring,
    IReadOnlyList<LiveOption> Running,
    int VideoPkid,
    string VideoName,
    string VideoPath,
    string ChannelName,
    string PlatformStreamName,
    string? StreamUrl,
    long PositionMilliseconds,
    long DurationMilliseconds,
    bool IsPlayable,
    LiveMedia Source,
    LiveMedia Output,
    LiveParameters Parameters,
    IReadOnlyList<LiveSource>? Sources = null)
{
    public static LiveSnapshot Offline(IReadOnlyList<LiveOption> running) => new(
        false,
        false,
        running,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        0,
        0,
        false,
        Empty,
        Empty,
        EmptyParameters);

    private static readonly LiveMedia Empty = new(0, 0, 0, false, 0);

    private static readonly LiveParameters EmptyParameters = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// What the preview page is built on: the state of the running transcodes, read from the ffmpeg
/// sessions instead of the database, so the position is the one the encoder is really at.
///
/// It also changes the configuration of a live that is already running. ffmpeg cannot swap its
/// options halfway through a file, so a change is written to the video setting and the transcode is
/// asked to start again from the position it reached: the only honest way to make a new bitrate or
/// a new resolution take effect while the live is on air.
/// </summary>
public sealed class LivePreviewService
{
    /// <summary>What a bitrate has to stay between, in bits per second.</summary>
    private const int MinimumBitrate = 1;

    private const int MaximumVideoBitrate = 1_000_000_000;
    private const int MaximumAudioBitrate = 1_000_000;

    /// <summary>A frame no platform would accept is refused before it reaches ffmpeg.</summary>
    private const int MaximumWidth = 7680;

    private const int MaximumHeight = 4320;
    private const double MaximumFrameRate = 240;

    /// <summary>Twice the sound of the source is as loud as a source is let go in the mix.</summary>
    public const int MaximumVolume = 200;

    private readonly StreamingSessionRegistry _sessions;
    private readonly VideoRepository _videoRepository;
    private readonly VideoSettingRepository _videoSettingRepository;
    private readonly VideoLiveHistoryRepository _historyRepository;
    private readonly LivePlatformEmbeds _embeds;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly LiveChangeNotifier _notifier;
    private readonly ILogger<LivePreviewService> _logger;

    public LivePreviewService(
        StreamingSessionRegistry sessions,
        VideoRepository videoRepository,
        VideoSettingRepository videoSettingRepository,
        VideoLiveHistoryRepository historyRepository,
        LivePlatformEmbeds embeds,
        ResponseFactory responses,
        Localizer localizer,
        LiveChangeNotifier notifier,
        ILogger<LivePreviewService> logger)
    {
        _sessions = sessions;
        _videoRepository = videoRepository;
        _videoSettingRepository = videoSettingRepository;
        _historyRepository = historyRepository;
        _embeds = embeds;
        _responses = responses;
        _localizer = localizer;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// The platform player of the live the page is watching, so the page can show the live as the
    /// viewers see it. Nothing when the live is not running, when the platform is one this
    /// application does not have a player for, or when the channel is not on air: the page falls
    /// back to the picture ffmpeg writes, which is the truth of the encoder either way.
    /// <para>A channel that is off air and a platform this application failed to reach are told
    /// apart here, because the page has something else to say about the second.</para>
    /// </summary>
    /// <param name="host">The host the page is served on, which Twitch has to be told about.</param>
    public async Task<LivePlatformEmbeds.LivePlatformLookup> EmbedAsync(
        int? videoPkid, string? host, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(videoPkid);
        return snapshot.IsLive
            ? await _embeds.ResolveAsync(snapshot.StreamUrl, snapshot.ChannelName, snapshot.PlatformStreamName, host, cancellationToken)
            : LivePlatformEmbeds.LivePlatformLookup.Nothing;
    }

    /// <summary>The live to watch: the one asked for while it runs, otherwise the last one started.</summary>
    public LiveSnapshot Snapshot(int? videoPkid)
    {
        var session = _sessions.Watched(videoPkid);
        if (session is null)
        {
            // No live running - try to show the requested video or the last one with its stored duration
            if (videoPkid is { } pkid)
            {
                var vid = _videoRepository.FindByPkid(pkid);
                if (vid is not null)
                {
                    var running = Running(null);
                    var durationMs = vid.DurationMilliseconds ?? 0;
                    var isPlayable = vid.ScenePkid is null && VideoExtensions.IsBrowserPlayable(vid.Extension);
                    return new LiveSnapshot(
                        false,
                        false,
                        running,
                        vid.Pkid,
                        vid.Name,
                        vid.VideoPath,
                        vid.ChannelName,
                        string.Empty,
                        null,
                        0,
                        durationMs,
                        isPlayable,
                        new LiveMedia(0, 0, 0, false, 0),
                        new LiveMedia(0, 0, 0, false, 0),
                        new LiveParameters(null, null, null, null, null, null, null, null, null, null, null, null, null));
                }
            }
            return LiveSnapshot.Offline(Running(null));
        }

        var video = _videoRepository.FindByPkid(session.VideoPkid);
        if (video is null)
        {
            return LiveSnapshot.Offline(Running(null));
        }

        var setting = SettingOf(video);
        var history = video.VideoLiveHistoryId is { } historyId ? _historyRepository.FindByPkid(historyId) : null;
        var probe = session.Probe;

        // For scene lives, the probe duration is 0 (synthetic). Use the sum of all video durations in the live history.
        long totalDurationMs = (long)(probe.DurationSeconds * 1000);
        if (totalDurationMs == 0 && video.VideoLiveHistoryId is { } liveHistoryId)
        {
            var videosInHistory = _videoRepository.FindByLiveHistoryId(liveHistoryId);
            totalDurationMs = videosInHistory.Sum(v => v.DurationMilliseconds ?? 0);
        }

        return new LiveSnapshot(
            true,
            session.RestartRequested,
            Running(session.VideoPkid),
            video.Pkid,
            video.Name,
            video.VideoPath,
            video.ChannelName,
            history?.PlatformStreamName ?? string.Empty,
            history?.StreamUrl,
            session.PositionMilliseconds,
            totalDurationMs,
            // A canvas is not any one of its files: playing the first of them would show a
            // picture that is not the one on air.
            video.ScenePkid is null && VideoExtensions.IsBrowserPlayable(video.Extension),
            new LiveMedia(probe.Width, probe.Height, probe.FrameRate, probe.HasAudio, probe.AudioChannels),
            new LiveMedia(session.Output.Width, session.Output.Height, session.Output.FrameRate, probe.HasAudio, probe.AudioChannels),
            ParametersOf(setting, probe),
            SourcesOf(video));
    }

    /// <summary>
    /// The sources of the canvas a live is streaming whose sound is in the mix: those are the ones
    /// a volume means something for. A live of a single file has none.
    /// </summary>
    private IReadOnlyList<LiveSource> SourcesOf(VideoEntity video)
    {
        if (video.ScenePkid is not { } scenePkid || video.VideoLiveHistoryId is not { } historyId)
        {
            return [];
        }

        return _videoRepository.FindByLiveHistoryId(historyId)
            .Where(row => row.ScenePkid == scenePkid && row.AudioEnabled)
            .OrderBy(row => row.Pkid)
            .Select(row => new LiveSource(row.Pkid, row.Name, row.SourceKind.ToString(), row.Volume))
            .ToList();
    }

    /// <summary>
    /// Sets how loud one source of a canvas is in the mix of the live, and starts the pass again
    /// for the mix to take it. The pass goes out on the connection the live already has, so the
    /// viewers hear the new level without the live going off air.
    /// </summary>
    public MessageResponse ApplyVolume(int videoPkid, LiveVolumeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_sessions.TryGet(videoPkid, out var session) || session is null || session.HasExited)
        {
            _logger.LogWarning("{Message}", _localizer.PrintMessage("live.not.streaming"));
            throw new NotFoundCustomException("live.not.streaming");
        }

        var video = _videoRepository.FindByPkid(videoPkid)
            ?? throw new NotFoundCustomException("video.not.found");

        // Only a source of the canvas this live is streaming: a pkid of any other row would
        // change a live the page is not looking at.
        var source = SourcesOf(video).FirstOrDefault(candidate => candidate.Pkid == request.SourcePkid)
            ?? throw new NotFoundCustomException("video.not.found");

        if (!InRange(request.Volume, 0, MaximumVolume))
        {
            throw new RequestValidationException(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["volume"] = Message("live.parameter.range", "volume", Number(0), Number(MaximumVolume))
            });
        }

        if (source.Volume == request.Volume)
        {
            return _responses.Build("live.parameters.unchanged", StatusCodes.Status200OK);
        }

        _videoRepository.SetVolume(source.Pkid, request.Volume);
        _sessions.RequestRestart(videoPkid);
        _notifier.Raise();

        _logger.LogInformation(
            "Live {Pkid}: volume of {Source} {Before}% -> {After}%", videoPkid, source.Name, source.Volume, request.Volume);
        return _responses.Build("live.parameters.applied", StatusCodes.Status200OK);
    }

    /// <summary>
    /// Writes the parameters of a running live and starts its transcode again. A request that
    /// changes nothing is answered as a success without touching the ffmpeg process: restarting it
    /// for no reason would cost the viewer a second of the live.
    /// </summary>
    public MessageResponse ApplyParameters(int videoPkid, LiveParameterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_sessions.TryGet(videoPkid, out var session) || session is null || session.HasExited)
        {
            _logger.LogWarning("{Message}", _localizer.PrintMessage("live.not.streaming"));
            throw new NotFoundCustomException("live.not.streaming");
        }

        var video = _videoRepository.FindByPkid(videoPkid);
        if (video is null)
        {
            throw new NotFoundCustomException("video.not.found");
        }

        var setting = SettingOf(video)
            ?? throw new NotFoundCustomException("video.settings.not.found");

        var before = ParametersOf(setting, session.Probe);
        Apply(setting, request, session.Probe);
        var after = ParametersOf(setting, session.Probe);

        if (after == before)
        {
            return _responses.Build("live.parameters.unchanged", StatusCodes.Status200OK);
        }

        setting.LastModified = DateTime.Now;
        _videoSettingRepository.Update(setting);
        _sessions.RequestRestart(videoPkid);
        _notifier.Raise();

        _logger.LogInformation(
            "Live {Pkid} reconfigured: {Before} -> {After}", videoPkid, before, after);
        return _responses.Build("live.parameters.applied", StatusCodes.Status200OK);
    }

    /// <summary>The file a live is sending, when that live is the one still running.</summary>
    public (string Path, string ContentType, string Extension) FileOf(int videoPkid)
    {
        if (!_sessions.TryGet(videoPkid, out var session) || session is null || session.HasExited)
        {
            throw new NotFoundCustomException("live.not.streaming");
        }

        var video = _videoRepository.FindByPkid(videoPkid);
        if (video is null)
        {
            throw new NotFoundCustomException("video.not.found");
        }

        if (!File.Exists(video.VideoPath))
        {
            throw new NotFoundCustomException("file.not.found");
        }

        return (video.VideoPath, VideoExtensions.ContentTypeOf(video.Extension), video.Extension);
    }

    private IReadOnlyList<LiveOption> Running(int? watching)
    {
        var options = new List<LiveOption>();
        foreach (var session in _sessions.Running)
        {
            if (session.HasExited)
            {
                continue;
            }

            var video = _videoRepository.FindByPkid(session.VideoPkid);
            if (video is null)
            {
                continue;
            }

            options.Add(new LiveOption(video.Pkid, video.ChannelName, video.Name, video.Pkid == watching));
        }

        return options;
    }

    private VideoSettingEntity? SettingOf(VideoEntity video) =>
        video.VideoSettingId is { } id ? _videoSettingRepository.FindById(id) : null;

    private static LiveParameters ParametersOf(VideoSettingEntity? setting, MediaProbeResult probe)
    {
        if (setting is null)
        {
            return LiveSnapshot.Offline([]).Parameters;
        }

        return new LiveParameters(
            setting.VideoCodec,
            FfmpegCodecCatalog.LabelOf(FfmpegCodecCatalog.VideoCodecs, setting.VideoCodec),
            FfmpegCodecCatalog.ResolveVideoCodecName(setting.VideoCodec, setting.VideoCodecName),
            setting.PixelFormat,
            FfmpegCodecCatalog.ResolvePixelFormat(setting.PixelFormat),
            setting.VideoBitrate,
            setting.GopSize,
            setting.AudioSetting?.AudioCodec,
            FfmpegCodecCatalog.LabelOf(FfmpegCodecCatalog.AudioCodecs, setting.AudioSetting?.AudioCodec),
            setting.AudioSetting?.AudioBitrate,
            setting.VideoWidth,
            setting.VideoHeight,
            setting.FrameRate is > 0 ? setting.FrameRate : null);
    }

    /// <summary>
    /// The fields that travelled with the request land on the setting; the ones that did not are
    /// left exactly as they were. Every value is checked before anything is written, so a rejected
    /// change cannot leave the configuration half updated.
    /// </summary>
    private void Apply(VideoSettingEntity setting, LiveParameterRequest request, MediaProbeResult probe)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (request.VideoCodec is { } videoCodec)
        {
            if (FfmpegCodecCatalog.LabelOf(FfmpegCodecCatalog.VideoCodecs, videoCodec) is null)
            {
                errors["videoCodec"] = Message("live.parameter.unknown", Number(videoCodec));
            }
            else
            {
                setting.VideoCodec = videoCodec;
                // The encoder name is what ffmpeg receives, so an abstract codec on its own would
                // change nothing: the encoder that goes with it travels with it.
                setting.VideoCodecName = FfmpegCodecCatalog.ResolveVideoCodecName(videoCodec, null);
            }
        }

        if (request.VideoCodecName is { } encoder && !string.IsNullOrWhiteSpace(encoder))
        {
            var name = encoder.Trim();
            if (!FfmpegCodecCatalog.VideoCodecNames.Any(vendor => vendor.Names.Contains(name, StringComparer.Ordinal)))
            {
                errors["videoCodecName"] = Message("live.parameter.unknown", name);
            }
            else
            {
                setting.VideoCodecName = name;
                // A hardware encoder the catalog does not list leaves the abstract codec alone.
                setting.VideoCodec = FfmpegCodecCatalog.IdOfEncoderName(name) ?? setting.VideoCodec;
            }
        }

        if (request.PixelFormat is { } pixelFormat)
        {
            if (FfmpegCodecCatalog.LabelOf(FfmpegCodecCatalog.PixelFormats, pixelFormat) is null)
            {
                errors["pixelFormat"] = Message("live.parameter.unknown", Number(pixelFormat));
            }
            else
            {
                setting.PixelFormat = pixelFormat;
            }
        }

        if (request.VideoBitrate is { } videoBitrate)
        {
            if (InRange(videoBitrate, MinimumBitrate, MaximumVideoBitrate))
            {
                setting.VideoBitrate = videoBitrate;
            }
            else
            {
                errors["videoBitrate"] = Message("live.parameter.range", "videoBitrate", Number(MinimumBitrate), Number(MaximumVideoBitrate));
            }
        }

        if (request.AudioBitrate is { } audioBitrate)
        {
            if (InRange(audioBitrate, MinimumBitrate, MaximumAudioBitrate))
            {
                if (setting.AudioSetting is null)
                {
                    errors["audioBitrate"] = Message("live.parameter.no.audio");
                }
                else
                {
                    setting.AudioSetting.AudioBitrate = audioBitrate;
                }
            }
            else
            {
                errors["audioBitrate"] = Message("live.parameter.range", "audioBitrate", Number(MinimumBitrate), Number(MaximumAudioBitrate));
            }
        }

        ApplyResolution(setting, request, errors, _localizer);
        ApplyFrameRate(setting, request, errors, _localizer);

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    /// <summary>
    /// The two halves of a resolution travel together: an empty pair keeps the one of the file and
    /// is told apart from "keep the source" only by both of them being absent. Zero on both halves
    /// is that decision: the page has to be able to ask for the source resolution again after
    /// having set one, and an absent field here means "leave it as it is".
    /// </summary>
    private static void ApplyResolution(
        VideoSettingEntity setting,
        LiveParameterRequest request,
        IDictionary<string, string> errors,
        Localizer localizer)
    {
        if (request.VideoWidth is null && request.VideoHeight is null)
        {
            return;
        }

        if (request.VideoWidth is { } width && request.VideoHeight is { } height && width == 0 && height == 0)
        {
            setting.VideoWidth = null;
            setting.VideoHeight = null;
            return;
        }

        if (request.VideoWidth is not { } askedWidth || request.VideoHeight is not { } askedHeight)
        {
            errors["videoWidth"] = localizer.PrintMessage("live.parameter.resolution.pair");
            return;
        }

        if (!InRange(askedWidth, 2, MaximumWidth) || !InRange(askedHeight, 2, MaximumHeight))
        {
            errors["videoWidth"] = localizer.PrintMessage(
                "live.parameter.range",
                ["resolution", "2x2", $"{Number(MaximumWidth)}x{Number(MaximumHeight)}"]);
            return;
        }

        setting.VideoWidth = askedWidth;
        setting.VideoHeight = askedHeight;
    }

    /// <summary>Zero is the frame rate of the source, the way an empty field on the page reads.</summary>
    private static void ApplyFrameRate(
        VideoSettingEntity setting,
        LiveParameterRequest request,
        IDictionary<string, string> errors,
        Localizer localizer)
    {
        if (request.FrameRate is not { } frameRate)
        {
            return;
        }

        if (frameRate == 0)
        {
            setting.FrameRate = null;
            return;
        }

        if (frameRate is < 0 or > MaximumFrameRate)
        {
            errors["frameRate"] = localizer.PrintMessage(
                "live.parameter.range", ["frameRate", "1", Number(MaximumFrameRate)]);
            return;
        }

        setting.FrameRate = frameRate;
    }

    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;

    private string Message(string code, params object?[] parameters) => _localizer.PrintMessage(code, parameters);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
