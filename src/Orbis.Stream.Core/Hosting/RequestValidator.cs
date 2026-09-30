using Microsoft.AspNetCore.Http;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.I18n;

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
        AddIfNull(errors, "streamKey", request.StreamKey, "not.valid.input");
        AddIfNull(errors, "platformStreamName", request.PlatformStreamName, "not.valid.input");
        AddIfNull(errors, "description", request.Description, "not.valid.input");
        AddIfNull(errors, "videoFolder", request.VideoFolder, "not.valid.input");
        AddIfNull(errors, "channelName", request.ChannelName, "not.valid.input");
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
