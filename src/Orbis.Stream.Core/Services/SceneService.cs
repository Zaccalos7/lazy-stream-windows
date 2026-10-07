using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// Saving and reading back the canvases. The layout is validated here rather than in the page,
/// because a canvas the encoder cannot open is a canvas the user finds out about too late: the
/// check has to be the same whoever calls the API.
/// </summary>
public sealed class SceneService
{
    private readonly SceneRepository _sceneRepository;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly ILogger<SceneService> _logger;

    public SceneService(
        SceneRepository sceneRepository,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<SceneService> logger)
    {
        _sceneRepository = sceneRepository;
        _responses = responses;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>The layouts only: the scene of a live belongs to its row in the history.</summary>
    public IReadOnlyList<SceneRequest> GetLayouts()
    {
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.retrieved"));
        return [.. _sceneRepository.FindLayouts().Select(SceneRequest.FromEntity)];
    }

    public SceneRequest GetOne(long pkid)
    {
        var scene = FindByPkid(pkid);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.retrieved"));
        return SceneRequest.FromEntity(scene);
    }

    /// <summary>
    /// A save never writes over what it is not meant to. A layout and the scene of a live are kept
    /// apart, so a save that names one as the other makes a new one; and the scene a live already
    /// went on air with is what that live restarts from, so changing it makes a new one too.
    /// </summary>
    public (MessageResponse Response, long Pkid) Save(SceneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Pkid is { } pkid)
        {
            var existing = FindByPkid(pkid);
            if (existing.IsLayout != request.IsLayout || (!existing.IsLayout && _sceneRepository.IsOnAir(pkid)))
            {
                request = request with { Pkid = null };
            }
        }

        var scene = request.ToEntity();
        if (scene.IsLayout)
        {
            CheckSlots(scene);
        }
        else
        {
            CheckLayout(scene);
        }

        var saved = _sceneRepository.Save(scene);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.saved", [scene.Name]));

        return (_responses.Build("scene.saved", StatusCodes.Status201Created, [scene.Name]), saved);
    }

    /// <summary>Only what no live depends on: a live restarts from the scene it went on air with.</summary>
    public MessageResponse Delete(long pkid)
    {
        var scene = FindByPkid(pkid);
        if (!scene.IsLayout && _sceneRepository.IsOnAir(pkid))
        {
            throw new LiveException("scene.on.air", [scene.Name]);
        }

        _sceneRepository.Delete(pkid);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("delete.successful"));
        return _responses.Build("delete.successful", StatusCodes.Status200OK);
    }

    /// <summary>
    /// What the encoder needs and the page cannot be trusted to have done: a canvas to compose
    /// into, a source to lay over it, a file that is there, and rectangles that stay inside it.
    /// </summary>
    private void CheckLayout(SceneEntity scene)
    {
        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            throw new NotFoundCustomException("scene.name.required");
        }

        if (scene.Width is not { } width || width <= 0 || scene.Height is not { } height || height <= 0)
        {
            throw new NotFoundCustomException("scene.size.required");
        }

        var items = scene.Items;
        if (items.Count == 0)
        {
            throw new NotFoundCustomException("scene.empty");
        }

        if (!items.Any(item => item.SourceKind.HasPicture()))
        {
            throw new NotFoundCustomException("scene.no.picture", [scene.Name]);
        }

        var seen = new HashSet<(SourceKind, string)>(items.Select(item => (item.SourceKind, item.SourceTarget)));
        if (items.Count != seen.Count)
        {
            // The same device twice is two inputs of one camera, which dshow cannot open twice.
            // Two different sources of one kind (the desktop and a monitor, two files) are fine.
            throw new NotFoundCustomException("scene.duplicated.kind");
        }

        // A microphone linked to a camera (video=…:audio=…) is the same device as that microphone
        // on its own: dshow cannot open it for both.
        var microphones = items
            .Select(item => item.SourceKind switch
            {
                SourceKind.Microphone => item.SourceTarget,
                SourceKind.Camera when item.SourceTarget.IndexOf(":audio=", StringComparison.Ordinal) is var at and >= 0
                    => item.SourceTarget[(at + 1)..],
                _ => null
            })
            .OfType<string>()
            .ToList();
        if (microphones.Count != microphones.Distinct(StringComparer.Ordinal).Count())
        {
            throw new NotFoundCustomException("scene.duplicated.kind");
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.SourceTarget))
            {
                throw new NotFoundCustomException("scene.item.no.target");
            }

            if (item.SourceKind == SourceKind.File
                && !File.Exists(StreamingService.NormalizeUserPath(item.SourceTarget)))
            {
                throw new NotFoundCustomException("scene.item.file.missing", [item.SourceTarget]);
            }

            if (item.SourceKind.HasPicture() && (item.Width <= 0 || item.Height <= 0))
            {
                throw new NotFoundCustomException("scene.item.no.size", [item.Label ?? item.SourceTarget]);
            }

            if (item.SourceKind.HasPicture()
                && (item.X < 0 || item.Y < 0
                    || item.X + item.Width > scene.Width || item.Y + item.Height > scene.Height))
            {
                throw new NotFoundCustomException("scene.item.out.of.canvas", [item.Label ?? item.SourceTarget]);
            }
        }
    }

    /// <summary>
    /// A layout is only rectangles: a name, a canvas, and at least one slot that stays inside it.
    /// The sources are checked later, on the scene filled from it.
    /// </summary>
    private static void CheckSlots(SceneEntity scene)
    {
        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            throw new NotFoundCustomException("scene.name.required");
        }

        if (scene.Width is not { } width || width <= 0 || scene.Height is not { } height || height <= 0)
        {
            throw new NotFoundCustomException("scene.size.required");
        }

        if (scene.Items.Count == 0)
        {
            throw new NotFoundCustomException("layout.empty");
        }

        var position = 0;
        foreach (var item in scene.Items)
        {
            var name = item.Label ?? (++position).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (item.Width <= 0 || item.Height <= 0)
            {
                throw new NotFoundCustomException("scene.item.no.size", [name]);
            }

            if (item.X < 0 || item.Y < 0 || item.X + item.Width > width || item.Y + item.Height > height)
            {
                throw new NotFoundCustomException("scene.item.out.of.canvas", [name]);
            }
        }
    }

    private SceneEntity FindByPkid(long pkid) =>
        _sceneRepository.FindByPkid(pkid)
        ?? throw new NotFoundCustomException("scene.not.found", [pkid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
}
