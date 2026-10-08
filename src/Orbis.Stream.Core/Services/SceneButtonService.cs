using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>A button of the scene deck, as the panel and the preview draw it.</summary>
/// <param name="Kind"><c>VIDEO</c> (it goes back to the live at its end) or <c>IMAGE</c> (it stays until the live is resumed).</param>
/// <param name="MediaName">The name of its file in the scene media, which a save sends back.</param>
/// <param name="MediaLabel">The name the file had when it was added, which is what the panel shows.</param>
/// <param name="Still">A frame of the file, for the face of the button.</param>
/// <param name="Hotkey">Its key as <see cref="SceneHotkey"/> writes it; null when it has none.</param>
/// <param name="Available">Whether its file is still there: a button without one cannot go on air.</param>
public sealed record SceneButtonResponse(
    long Pkid, string Label, string Kind, string MediaName, string MediaLabel, string Still, string? Hotkey, bool Available);

/// <summary>A file just added to the scene media, for the button being edited.</summary>
public sealed record SceneMediaResponse(string Name, string Label, string Kind, string Still, long Size);

/// <summary>
/// The scene deck: the buttons that put a video, a banner or an image on air in place of a live,
/// and the files they carry.
/// <para>The files are uploaded into a folder of the application (see <see cref="MediaStore"/>),
/// for the reason the overlays are: the deck shows a frame of each, and a route that drew a frame
/// of any file named by a path would draw the pictures of the whole disk for anyone on the network.
/// A button also outlives the folder its file was saved to.</para>
/// <para>A file is kept for as long as a button carries it or a live has it on air, and a file
/// added for a button that was never saved goes after a day.</para>
/// </summary>
public sealed class SceneButtonService
{
    public const string DirectoryName = "scene-media";

    /// <summary>How many buttons the deck holds: two rows of three, the size of the smallest Stream Deck.</summary>
    public const int MaxButtons = 6;

    public const int MaxLabelLength = 40;

    /// <summary>The largest file taken. A clip of a few minutes in 1080p is a few hundred megabytes.</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private const int StillWidth = 480;

    /// <summary>How long a file nothing uses waits before it goes: the time to finish the form it was added from.</summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    /// <summary>The pictures: they stay on air until the live is resumed.</summary>
    private static readonly Dictionary<string, string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".apng"] = "image/apng",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp"
    };

    /// <summary>The clips: they play once and the live comes back.</summary>
    private static readonly Dictionary<string, string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".flv"] = "video/x-flv",
        [".ts"] = "video/mp2t",
        [".mts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".wmv"] = "video/x-ms-wmv"
    };

    /// <summary>The extensions the panel offers in its file picker.</summary>
    public static readonly string Accept = string.Join(',', Videos.Keys.Concat(Images.Keys));

    private readonly MediaStore _store;
    private readonly SceneButtonRepository _repository;
    private readonly FfmpegProbe _probe;
    private readonly SourceSnapshotService _snapshots;
    private readonly LiveTakeovers _takeovers;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly ILogger<SceneButtonService> _logger;

    public SceneButtonService(
        OrbisRuntimeOptions options,
        SceneButtonRepository repository,
        FfmpegProbe probe,
        SourceSnapshotService snapshots,
        LiveTakeovers takeovers,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<SceneButtonService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = new MediaStore(
            Path.Combine(options.DataDirectory, DirectoryName),
            Videos.Concat(Images).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            MaxBytes,
            "scene",
            logger);
        _repository = repository;
        _probe = probe;
        _snapshots = snapshots;
        _takeovers = takeovers;
        _responses = responses;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>What a file does on air, judged on its extension: a picture stays, anything else is a clip.</summary>
    public static SceneButtonKind KindOf(string fileName) =>
        Images.ContainsKey(Path.GetExtension(fileName)) ? SceneButtonKind.Image : SceneButtonKind.Video;

    public IReadOnlyList<SceneButtonResponse> List() => [.. _repository.FindAll().Select(ResponseOf)];

    public SceneButtonEntity? Find(long pkid) => _repository.FindByPkid(pkid);

    /// <summary>The file a button puts on air; null when it is gone.</summary>
    public string? PathOf(SceneButtonEntity button)
    {
        ArgumentNullException.ThrowIfNull(button);
        return _store.PathOf(button.MediaName);
    }

    /// <summary>
    /// Copies a file into the scene media for a button to carry. It is checked before it is given
    /// a name: a file with the extension of a video and something else inside is refused here,
    /// where the user is looking, rather than when the live tries to put it on air.
    /// </summary>
    public async Task<SceneMediaResponse> AddMediaAsync(string? fileName, System.IO.Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var label = Path.GetFileName(fileName ?? string.Empty);
        if (!_store.Accepts(label))
        {
            throw Refused("file", "scene.media.not.valid", label);
        }

        // The files left by forms that were never saved go first, so the folder does not grow
        // with every file the user changed their mind about.
        _store.Sweep(OrphanAge, name => _repository.UsesMedia(name) || IsOnAir(name));

        var name = await _store.AddAsync(
                label,
                content,
                (upload, token) => CheckMediaAsync(upload, label, token),
                () => Refused("file", "scene.media.too.large", label, MaxBytes / (1024 * 1024)),
                cancellationToken)
            .ConfigureAwait(false);

        var path = _store.PathOf(name)!;
        return new SceneMediaResponse(name, MediaStore.LabelOf(name), KindOf(name).ToWireValue(), StillUrl(name), new FileInfo(path).Length);
    }

    public SceneButtonResponse Create(SceneButtonRequest? request)
    {
        var button = Validated(request, null);
        if (_repository.Count() >= MaxButtons)
        {
            throw new LiveException("scene.button.limit", [MaxButtons]);
        }

        button.LastModified = DateTime.Now;
        _repository.Insert(button);
        _logger.LogInformation("Scene button {Label} added", button.Label);
        return ResponseOf(button);
    }

    public SceneButtonResponse Update(long pkid, SceneButtonRequest? request)
    {
        var existing = _repository.FindByPkid(pkid)
            ?? throw new NotFoundCustomException("scene.button.not.found", [pkid]);

        var button = Validated(request, pkid);
        button.Pkid = pkid;
        button.Position = existing.Position;
        button.LastModified = DateTime.Now;
        _repository.Update(button);

        if (!string.Equals(existing.MediaName, button.MediaName, StringComparison.Ordinal))
        {
            Release(existing.MediaName);
        }

        return ResponseOf(button);
    }

    public MessageResponse Delete(long pkid)
    {
        var existing = _repository.FindByPkid(pkid)
            ?? throw new NotFoundCustomException("scene.button.not.found", [pkid]);

        _repository.Delete(pkid);
        Release(existing.MediaName);
        _logger.LogInformation("Scene button {Label} deleted", existing.Label);
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    /// <summary>
    /// The face of a button: the first frame of a picture as a PNG, with its clear parts clear, or
    /// a frame of a clip a second in, past the black most videos open on. A clip shorter than that
    /// gives its first frame. Null when ffmpeg cannot read the file either.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> StillAsync(string? name, CancellationToken cancellationToken)
    {
        if (_store.PathOf(name) is not { } path)
        {
            return null;
        }

        if (KindOf(path) == SceneButtonKind.Video
            && await _snapshots.GrabAsync(SourceKind.File, path, cancellationToken).ConfigureAwait(false) is { } frame)
        {
            return (frame, "image/jpeg");
        }

        var still = await _snapshots.GrabOverlayAsync(path, OverlayMedia.Still, cancellationToken, StillWidth).ConfigureAwait(false);
        return still is null ? null : (still, "image/png");
    }

    private SceneButtonEntity Validated(SceneButtonRequest? request, long? pkid)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var label = request?.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > MaxLabelLength)
        {
            errors["label"] = _localizer.PrintMessage("scene.button.label.not.valid", [MaxLabelLength]);
        }

        var media = request?.MediaName?.Trim() ?? string.Empty;
        if (media.Length == 0)
        {
            errors["mediaName"] = _localizer.PrintMessage("scene.button.media.required");
        }
        else if (_store.PathOf(media) is null)
        {
            errors["mediaName"] = _localizer.PrintMessage("scene.button.media.missing", [label.Length > 0 ? label : media]);
        }

        var hotkey = string.IsNullOrWhiteSpace(request?.Hotkey) ? null : request.Hotkey.Trim();
        if (hotkey is not null)
        {
            if (!SceneHotkey.IsValid(hotkey))
            {
                errors["hotkey"] = _localizer.PrintMessage("scene.button.hotkey.not.valid", [hotkey]);
            }
            else if (_repository.FindAll().FirstOrDefault(other => other.Pkid != pkid && other.Hotkey == hotkey) is { } taken)
            {
                errors["hotkey"] = _localizer.PrintMessage("scene.button.hotkey.taken", [hotkey, taken.Label]);
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return new SceneButtonEntity { Label = label, MediaName = media, Hotkey = hotkey };
    }

    /// <summary>
    /// A file a button no longer carries goes, unless another button carries it too, or a live has
    /// it on air right now: that one stays for the sweep of a later day.
    /// </summary>
    private void Release(string mediaName)
    {
        if (_repository.UsesMedia(mediaName) || IsOnAir(mediaName))
        {
            return;
        }

        _store.TryDelete(mediaName);
    }

    private bool IsOnAir(string mediaName) => _store.PathOf(mediaName) is { } path && _takeovers.Uses(path);

    /// <summary>
    /// A file goes in when ffprobe finds a picture in it, a still or a moving one. Without ffprobe
    /// nothing can be checked, and the file is taken on trust.
    /// </summary>
    private async Task CheckMediaAsync(string path, string label, CancellationToken cancellationToken)
    {
        MediaProbeResult probe;
        try
        {
            probe = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _logger.LogWarning(exception, "ffprobe is not available: {Label} joins the scene media unchecked", label);
            return;
        }
        catch (InvalidOperationException)
        {
            throw Refused("file", "scene.media.not.valid", label);
        }

        if (probe.Width <= 0 || probe.Height <= 0)
        {
            throw Refused("file", "scene.media.not.valid", label);
        }
    }

    private SceneButtonResponse ResponseOf(SceneButtonEntity button) => new(
        button.Pkid,
        button.Label,
        KindOf(button.MediaName).ToWireValue(),
        button.MediaName,
        MediaStore.LabelOf(button.MediaName),
        StillUrl(button.MediaName),
        button.Hotkey,
        _store.PathOf(button.MediaName) is not null);

    private static string StillUrl(string name) => "/scene-buttons/media/" + Uri.EscapeDataString(name) + "/still";

    /// <summary>A refusal about a field, answered as the field errors of any other form.</summary>
    private RequestValidationException Refused(string field, string code, params object?[] parameters) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [field] = _localizer.PrintMessage(code, parameters) });
}
