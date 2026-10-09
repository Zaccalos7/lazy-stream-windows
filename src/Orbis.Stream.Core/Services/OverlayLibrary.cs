using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Services;

/// <summary>One picture of the overlay library, as the layout page lists it.</summary>
/// <param name="Name">The file name in the library: what every other route of it is addressed by.</param>
/// <param name="Label">The name the file had when it was added, which is what the page shows.</param>
/// <param name="Path">The full path, which is what a layout stores and what ffmpeg opens.</param>
/// <param name="Url">The file itself, for the browser to draw.</param>
/// <param name="Still">Its first frame as a PNG, for a video the browser cannot play.</param>
/// <param name="Video">Whether it is drawn with a video element rather than an image.</param>
/// <param name="Size">Its size in bytes.</param>
public sealed record OverlayEntry(string Name, string Label, string Path, string Url, string Still, bool Video, long Size);

/// <summary>A file of the library as a route answers with it.</summary>
public sealed record OverlayFile(string Path, string ContentType);

/// <summary>
/// The pictures layouts are dressed with: logos, frames, the animated overlays sold for
/// Streamlabs and OBS. They are copied into a folder of the application rather than read where the
/// user keeps them, for two reasons.
/// <para>The server listens on every interface with an open CORS policy, so a route that answered
/// with any picture named by a path would hand the pictures of the whole disk to anyone on the
/// network. The page uploads the file instead, and the routes of the library only ever read from
/// this folder.</para>
/// <para>A layout outlives the folder of Downloads an overlay was saved to: the copy is what keeps
/// the live of next month the same as the live of today.</para>
/// <para>The file is named after its content as well as after its name, so the same overlay added
/// twice is one file, and a name is never reused for another picture: the browser may keep it.</para>
/// </summary>
public sealed class OverlayLibrary
{
    public const string DirectoryName = "overlays";

    /// <summary>The largest file taken. A minute of a 1080p WebM with alpha is a few tens of megabytes.</summary>
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>What can be laid over a canvas, and how the browser is told what it is.</summary>
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".apng"] = "image/apng",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime"
    };

    /// <summary>The extensions the page offers in its file picker, in the order it lists them.</summary>
    public static readonly string Accept = string.Join(',', ContentTypes.Keys);

    private readonly MediaStore _store;
    private readonly FfmpegProbe _probe;
    private readonly SourceSnapshotService _snapshots;
    private readonly SceneRepository _scenes;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly ILogger<OverlayLibrary> _logger;

    public OverlayLibrary(
        OrbisRuntimeOptions options,
        FfmpegProbe probe,
        SourceSnapshotService snapshots,
        SceneRepository scenes,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<OverlayLibrary> logger)
        : this(System.IO.Path.Combine(options.DataDirectory, DirectoryName), probe, snapshots, scenes, responses, localizer, logger)
    {
    }

    public OverlayLibrary(
        string directory,
        FfmpegProbe probe,
        SourceSnapshotService snapshots,
        SceneRepository scenes,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<OverlayLibrary> logger)
    {
        _store = new MediaStore(directory, ContentTypes, MaxBytes, "overlay", logger);
        _probe = probe;
        _snapshots = snapshots;
        _scenes = scenes;
        _responses = responses;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>Whether a file of that name can be an overlay at all, judged on its extension.</summary>
    public static bool IsOverlayFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && ContentTypes.ContainsKey(System.IO.Path.GetExtension(fileName));

    /// <summary>The newest first: the one just added is the one about to be used.</summary>
    public IReadOnlyList<OverlayEntry> List() => [.. _store.Files().Select(EntryOf)];

    /// <summary>
    /// Copies a file into the library and answers with what it is there. The copy is written beside
    /// its final name and renamed onto it once complete and checked, so a library never lists a
    /// half-written file, and an overlay that is already there is kept as it is.
    /// </summary>
    public async Task<OverlayEntry> AddAsync(string fileName, System.IO.Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var label = System.IO.Path.GetFileName(fileName ?? string.Empty);
        if (!IsOverlayFile(label))
        {
            throw Refused("overlay.not.valid", label);
        }

        var name = await _store.AddAsync(
                label,
                content,
                (upload, token) => CheckPictureAsync(upload, label, token),
                () => Refused("overlay.too.large", label, MaxBytes / (1024 * 1024)),
                cancellationToken)
            .ConfigureAwait(false);

        return EntryOf(new FileInfo(System.IO.Path.Combine(_store.Directory, name)));
    }

    /// <summary>A file of the library, by its name: nothing outside the folder is ever answered.</summary>
    public OverlayFile? Find(string? name) =>
        _store.PathOf(name) is { } path
            ? new OverlayFile(path, _store.ContentTypeOf(path))
            : null;

    /// <summary>Whether a path is a file of the library, the way a layout stores it.</summary>
    public bool Contains(string? path) => _store.Contains(path);

    /// <summary>
    /// The first frame of an overlay as a PNG: what the page draws for a video it cannot play,
    /// a MOV with alpha above all. Null when ffmpeg cannot read it either.
    /// </summary>
    public async Task<byte[]?> StillAsync(string? name, CancellationToken cancellationToken)
    {
        if (_store.PathOf(name) is not { } path)
        {
            return null;
        }

        var media = await MediaOfAsync(path, cancellationToken).ConfigureAwait(false);
        return await _snapshots.GrabOverlayAsync(path, media, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// How ffmpeg opens an overlay (see <see cref="OverlayMedia"/>). A file ffprobe cannot read is
    /// opened as a still: ffmpeg then says what is wrong with it, which is the message worth having.
    /// </summary>
    public async Task<OverlayMedia> MediaOfAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return OverlayMedia.Of(await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "Could not probe the overlay {Path}", path);
            return OverlayMedia.Still;
        }
    }

    /// <summary>
    /// Deletes an overlay nothing is using. One that a layout, or a scene a live went on air with,
    /// still names is kept: the layout would open with a hole in it and the live would restart
    /// without it.
    /// </summary>
    public MessageResponse Delete(string? name)
    {
        var path = _store.PathOf(name) ?? throw new NotFoundCustomException("overlay.not.found", [name]);
        if (_scenes.UsesOverlay(path))
        {
            throw new LiveException("overlay.in.use", [LabelOf(System.IO.Path.GetFileName(path))]);
        }

        File.Delete(path);
        _logger.LogInformation("Overlay {Name} deleted from the library", name);
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    /// <summary>
    /// A picture is checked before it joins the library, not when the live opens it: a file with
    /// the extension of a picture and something else inside is refused here, where the user is
    /// looking. Without ffprobe nothing can be checked, and the file is taken on trust.
    /// </summary>
    private async Task CheckPictureAsync(string path, string label, CancellationToken cancellationToken)
    {
        MediaProbeResult probe;
        try
        {
            probe = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _logger.LogWarning(exception, "ffprobe is not available: {Label} joins the library unchecked", label);
            return;
        }
        catch (InvalidOperationException)
        {
            throw Refused("overlay.not.valid", label);
        }

        if (probe.Width <= 0 || probe.Height <= 0)
        {
            throw Refused("overlay.not.valid", label);
        }
    }

    private static OverlayEntry EntryOf(FileInfo file)
    {
        var url = "/scene/overlays/" + Uri.EscapeDataString(file.Name);
        var extension = file.Extension.ToLowerInvariant();
        return new OverlayEntry(
            file.Name,
            LabelOf(file.Name),
            file.FullName,
            url,
            url + "/still",
            extension is ".webm" or ".mp4" or ".mov",
            file.Length);
    }

    /// <summary>The name the file was added with: the stored name less the hash of its content.</summary>
    internal static string LabelOf(string name) => MediaStore.LabelOf(name);

    /// <summary>The readable part of the stored name (see <see cref="MediaStore.StemOf"/>).</summary>
    internal static string StemOf(string label) => MediaStore.StemOf(label, "overlay");

    /// <summary>A refusal about the file, answered as the field errors of any other form.</summary>
    private RequestValidationException Refused(string code, params object?[] parameters) =>
        new(new Dictionary<string, string> { ["file"] = _localizer.PrintMessage(code, parameters) });
}
