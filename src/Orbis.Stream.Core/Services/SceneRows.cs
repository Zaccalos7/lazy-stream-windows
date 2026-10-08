using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// The rows a canvas was written out as (see StreamingService.StartSceneLive), read back as one live.
/// </summary>
public static class SceneRows
{
    /// <summary>
    /// The row a canvas stands on: its ffmpeg is registered under it, its preview is drawn under
    /// it, and the live page shows it. It is the first source with a picture, in stacking order. An
    /// overlay is a picture but not a source - a background image is usually the bottom of the
    /// stack - so it only stands for the live when there is nothing else to see. The live page picks
    /// its row with the same rule (VideoRepository.FindLivePage): the two have to agree, or a stop
    /// is addressed to a row nothing is streaming under.
    /// </summary>
    public static VideoEntity? BaseOf(IEnumerable<VideoEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var pictures = rows.Where(row => row.SourceKind.HasPicture()).ToList();
        return pictures.FirstOrDefault(row => !row.SourceKind.IsOverlay()) ?? pictures.FirstOrDefault();
    }
}
