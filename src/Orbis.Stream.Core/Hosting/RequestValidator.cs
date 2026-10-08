using Microsoft.AspNetCore.Http;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Reproduces the Bean Validation of the Java records: every violated constraint produces a
/// localized entry keyed by field name, answered with HTTP 400.
/// </summary>
public sealed class RequestValidator
{
    private readonly Localizer _localizer;

    public RequestValidator(Localizer localizer)
    {
        _localizer = localizer;
    }

    public void RequireSetting(SettingRequest? request)
    {
        if (request is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["body"] = Message("not.valid.input")
            });
        }

        var errors = new Dictionary<string, string>();
        AddIfNull(errors, "streamUrl", request.StreamUrl, "not.valid.input");
        // TikTok hands out a key for every live: its configuration has none to give (LiveStreamKeys).
        if (!LiveStreamKeys.IsAskedFor(request.PlatformStreamName, request.StreamUrl))
        {
            AddIfNull(errors, "streamKey", request.StreamKey, "not.valid.input");
        }

        AddIfNull(errors, "platformStreamName", request.PlatformStreamName, "not.valid.input");
        AddIfNull(errors, "channelName", request.ChannelName, "not.valid.input");

        // Auto-cleanup validation
        if (request.AutoCleanupEnabled)
        {
            if (request.AutoCleanupIntervalMonths <= 0)
            {
                errors["autoCleanupIntervalMonths"] = Message("not.valid.input");
            }
            // Zero is "older than yesterday" and minus one "older than now": both are periods.
            if (request.AutoCleanupOlderThanMonths < LiveHistoryCleanupService.Everything)
            {
                errors["autoCleanupOlderThanMonths"] = Message("not.valid.input");
            }
        }

        Throw(errors);
    }

    public void RequireStartLive(StartLiveRequest? request)
    {
        if (request is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["body"] = Message("input.not.valid")
            });
        }

        var errors = new Dictionary<string, string>();
        AddIfNull(errors, "streamUrl", request.StreamUrl, "not.valid.input");
        AddIfNull(errors, "streamKey", request.StreamKey, "not.valid.input");
        AddIfNull(errors, "videoPath", request.VideoPath, "not.valid.input");
        AddIfNull(errors, "platformStreamName", request.PlatformStreamName, "input.not.valid");
        AddIfNull(errors, "channelName", request.ChannelName, "input.not.valid");
        Throw(errors);
    }

    /// <summary>
    /// The same as a folder start, with a scene id where the path goes. A canvas carries its
    /// sources, so there is no folder to validate and nothing to guess at.
    /// </summary>
    public void RequireStartSceneLive(StartSceneLiveRequest? request)
    {
        if (request is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["body"] = Message("input.not.valid")
            });
        }

        var errors = new Dictionary<string, string>();
        AddIfNull(errors, "streamUrl", request.StreamUrl, "not.valid.input");
        AddIfNull(errors, "streamKey", request.StreamKey, "not.valid.input");
        AddIfNull(errors, "platformStreamName", request.PlatformStreamName, "input.not.valid");
        AddIfNull(errors, "channelName", request.ChannelName, "input.not.valid");

        if (request.ScenePkid <= 0)
        {
            errors["scenePkid"] = Message("not.valid.input");
        }

        Throw(errors);
    }

    public void RequireVideo(VideoRequest? request)
    {
        if (request is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["body"] = Message("input.not.valid")
            });
        }

        var errors = new Dictionary<string, string>();
        AddIfNull(errors, "pkid", request.Pkid, "input.not.valid");
        AddIfNull(errors, "name", request.Name, "input.not.valid");
        AddIfNull(errors, "videoPath", request.VideoPath, "input.not.valid");
        AddIfNull(errors, "extension", request.Extension, "input.not.valid");
        AddIfNull(errors, "liveStatus", request.LiveStatus, "input.not.valid");
        AddIfNull(errors, "videoLiveHistory", request.VideoLiveHistory, "input.not.valid");
        AddIfNull(errors, "videoSetting", request.VideoSetting, "input.not.valid");
        AddIfNull(errors, "channelName", request.ChannelName, "input.not.valid");
        Throw(errors);
    }

    /// <summary>
    /// The preview sends one change at a time and every field of it is optional, so the only thing
    /// to refuse is a request that carries nothing: it would answer as a success without having
    /// touched anything.
    /// </summary>
    public void RequireLiveParameters(LiveParameterRequest? request)
    {
        if (request is null || request is
            {
                VideoCodec: null, VideoCodecName: null, PixelFormat: null, VideoBitrate: null,
                AudioBitrate: null, VideoWidth: null, VideoHeight: null, FrameRate: null
            })
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["parameters"] = Message("live.parameters.empty")
            });
        }
    }

    public void RequireLiveVolume(LiveVolumeRequest? request)
    {
        if (request is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["volume"] = Message("live.parameters.empty")
            });
        }
    }

    public void RequireImage(IFormFile? image)
    {
        if (image is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["image"] = Message("input.not.valid")
            });
        }
    }

    /// <summary>
    /// A file for the overlay library: there, of a kind a canvas can lay over itself, and not
    /// bigger than the library takes. What is inside it is checked by the library, with ffprobe.
    /// </summary>
    public void RequireOverlay(IFormFile? file)
    {
        if (file is null)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["file"] = Message("input.not.valid")
            });
        }

        if (!OverlayLibrary.IsOverlayFile(file.FileName))
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["file"] = _localizer.PrintMessage("overlay.not.valid", [Path.GetFileName(file.FileName)])
            });
        }

        if (file.Length > OverlayLibrary.MaxBytes)
        {
            throw new RequestValidationException(new Dictionary<string, string>
            {
                ["file"] = _localizer.PrintMessage(
                    "overlay.too.large", [Path.GetFileName(file.FileName), OverlayLibrary.MaxBytes / (1024 * 1024)])
            });
        }
    }

    private void AddIfNull(IDictionary<string, string> errors, string field, object? value, string code)
    {
        if (value is null)
        {
            errors[field] = Message(code);
        }
    }

    private string Message(string code) => _localizer.PrintMessage(code);

    private static void Throw(Dictionary<string, string> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
