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

    public IReadOnlyList<SceneRequest> GetAll()
    {
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.retrieved"));
        return [.. _sceneRepository.FindAll().Select(SceneRequest.FromEntity)];
    }

    public SceneRequest GetOne(long pkid)
    {
        var scene = FindByPkid(pkid);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.retrieved"));
        return SceneRequest.FromEntity(scene);
    }

    public (MessageResponse Response, long Pkid) Save(SceneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Pkid is { } pkid)
        {
            FindByPkid(pkid);
        }

        var scene = request.ToEntity();
        CheckLayout(scene);

        var saved = _sceneRepository.Save(scene);
        _logger.LogInformation("{Message}", _localizer.PrintMessage("scene.saved", [scene.Name]));

        return (_responses.Build("scene.saved", StatusCodes.Status201Created, [scene.Name]), saved);
    }

    public MessageResponse Delete(long pkid)
    {
        FindByPkid(pkid);
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

    private SceneEntity FindByPkid(long pkid) =>
        _sceneRepository.FindByPkid(pkid)
        ?? throw new NotFoundCustomException("scene.not.found", [pkid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
}
