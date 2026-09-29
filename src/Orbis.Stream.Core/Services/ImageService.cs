using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Configuration;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

namespace Orbis.Stream.Core.Services;

/// <summary>Port of <c>com.orbis.stream.component.ImageComponent</c>.</summary>
public sealed class ImageService
{
    private const string ImageName = "HOME";

    private readonly OrbisRuntimeOptions _options;
    private readonly ResponseFactory _responses;
    private readonly Localizer _localizer;
    private readonly ILogger<ImageService> _logger;

    public ImageService(
        OrbisRuntimeOptions options,
        ResponseFactory responses,
        Localizer localizer,
        ILogger<ImageService> logger)
    {
        _options = options;
        _responses = responses;
        _localizer = localizer;
        _logger = logger;
    }

    public MessageResponse SaveImage(IFormFile image)
    {
        ArgumentNullException.ThrowIfNull(image);
        SaveImageOnDirectory(image);
        return _responses.Build("image.saved", StatusCodes.Status201Created);
    }

    public ImagePayload LoadImage()
    {
        var folder = _options.ImagesDirectory;
        if (!Directory.Exists(folder))
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("folder.not.found"));
            throw new FileReadingException("folder.not.found");
        }

        var file = Directory.EnumerateFiles(folder).FirstOrDefault();
        if (file is null)
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("resource.not.found"));
            throw new FileReadingException("resource.not.found");
        }

        var path = Path.GetFullPath(file).Replace('\\', '/');
        if (!File.Exists(path))
        {
            _logger.LogError("{Message}", _localizer.PrintMessage("resource.not.found"));
            throw new FileReadingException("resource.not.found");
        }

        var contentType = GetContentType(path);
        return new ImagePayload(path, contentType);
    }

    private void SaveImageOnDirectory(IFormFile file)
    {
        try
        {
            Directory.CreateDirectory(_options.ImagesDirectory);

            var imageName = file.FileName;
            var imageType = imageName.Split('.');
            var extension = imageType.Length > 1 ? imageType[^1] : string.Empty;

            var filePath = Path.Combine(_options.ImagesDirectory, $"{ImageName}.{extension}");

            // Only one image is kept: when the extension changes the previous file must go away,
            // otherwise the old one would be served again.
            var previousImage = Directory.Exists(_options.ImagesDirectory)
                ? Directory.EnumerateFiles(_options.ImagesDirectory).FirstOrDefault()
                : null;
            if (previousImage is not null)
            {
                File.Delete(previousImage);
            }

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            file.CopyTo(stream);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "{Message}", _localizer.PrintMessage("error.during.save.file"));
            throw new FileReadingException("error.during.save.file", null, exception);
        }
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
}

/// <summary>The stored image, served as a file like the Java <c>Resource</c> response did.</summary>
public sealed record ImagePayload(string Path, string ContentType);
