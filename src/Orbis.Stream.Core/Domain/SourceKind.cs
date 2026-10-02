using System.Globalization;

namespace Orbis.Stream.Core.Domain;

/// <summary>
/// What a row on the canvas is capturing. The Java version had one kind of source, a file path,
/// and every ffmpeg it started read exactly that; the enum is what tells the command builder
/// which of the capture devices to open instead.
/// </summary>
public enum SourceKind
{
    /// <summary>A file on disk, played like the rest of the playlist. The only kind the previous
    /// version knew about, and the default so that existing rows keep meaning what they meant.</summary>
    File = 0,

    /// <summary>A monitor, or the whole desktop, through ffmpeg's <c>gdigrab</c>.</summary>
    Screen = 1,

    /// <summary>A camera, through ffmpeg's <c>dshow</c>.</summary>
    Camera = 2,

    /// <summary>
    /// A microphone, through ffmpeg's <c>dshow</c>. It has no picture, so it takes no tile on the
    /// canvas: it only joins the audio mix. A camera's own device is a video-only entry, which is
    /// why the microphone a user wants alongside it is a source of its own.
    /// </summary>
    Microphone = 3
}

public static class SourceKindExtensions
{
    /// <summary>Wire/database name, e.g. <c>SCREEN</c>.</summary>
    public static string ToWireValue(this SourceKind kind) => kind switch
    {
        SourceKind.File => "FILE",
        SourceKind.Screen => "SCREEN",
        SourceKind.Camera => "CAMERA",
        SourceKind.Microphone => "MICROPHONE",
        _ => kind.ToString().ToUpperInvariant()
    };

    public static bool TryParseWireValue(string? value, out SourceKind kind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "FILE":
                kind = SourceKind.File;
                return true;
            case "SCREEN":
            case "DISPLAY":
            case "DESKTOP":
                kind = SourceKind.Screen;
                return true;
            case "CAMERA":
            case "WEBCAM":
            case "VIDEO":
                kind = SourceKind.Camera;
                return true;
            case "MICROPHONE":
            case "MIC":
            case "AUDIO":
                kind = SourceKind.Microphone;
                return true;
            default:
                kind = SourceKind.File;
                return false;
        }
    }

    /// <summary>
    /// Only a file has an end and a length: a capture device runs until it is told to stop, and
    /// ffprobe is not the thing that decides that, so these two are answered on kind alone.
    /// </summary>
    public static bool IsCaptureDevice(this SourceKind kind) =>
        kind is SourceKind.Screen or SourceKind.Camera or SourceKind.Microphone;

    /// <summary>Whether the source contributes a picture: a microphone is only heard.</summary>
    public static bool HasPicture(this SourceKind kind) => kind is not SourceKind.Microphone;

    /// <summary>Rejects a value that is not a real kind, so a request cannot invent one.</summary>
    public static bool IsDefined(this SourceKind kind) =>
        kind is SourceKind.File or SourceKind.Screen or SourceKind.Camera or SourceKind.Microphone;

    /// <summary>Accepts the wire names and the numbers, since both travel through the API.</summary>
    public static bool TryParse(string? value, out SourceKind kind)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && Enum.IsDefined(typeof(SourceKind), number))
        {
            kind = (SourceKind)number;
            return true;
        }

        return TryParseWireValue(value, out kind);
    }
}
