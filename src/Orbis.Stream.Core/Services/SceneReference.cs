using System.Globalization;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// How a canvas is written into the <c>folder_of_video_to_stream</c> column and into the path of
/// its rows. A live from a folder stores a path that exists on disk and this one stores a name
/// that does not, which is what keeps the two apart: the history is looked up by that column on a
/// restart, and a scene has to come back as a scene.
/// </summary>
public static class SceneReference
{
    public const string Prefix = "scene://";

    public static string Of(SceneEntity scene) => $"{Prefix}{scene.Pkid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The value a device row carries instead of a path. It has to start with a path separator for
    /// the rest of the application, which reads this column as a path to show and to probe.
    /// </summary>
    public static string ItemPath(SourceKind kind, string target) =>
        $"{Prefix}{kind.ToWireValue().ToLowerInvariant()}/{target.Replace('/', '∕')}";

    /// <summary>Whether this is the marker of a scene, as opposed to a real file or folder.</summary>
    public static bool IsScene(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith(Prefix, StringComparison.Ordinal);
}
