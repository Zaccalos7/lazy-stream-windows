namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// How ffmpeg opens an overlay, decided on what ffprobe read in it.
/// <para>A still is opened once and decoded once: the overlay filter keeps showing the last
/// picture of an input that has ended (its <c>eof_action</c> is <c>repeat</c>), so a 1080p PNG
/// costs a single decode for the whole live. Looping it instead would decode it again for every
/// frame, which for a large PNG is most of a core spent on a picture that never changes.</para>
/// <para>An animation is read again from its first frame whenever it ends, for as long as the live
/// lasts. A WebM with alpha also needs the libvpx decoder: the native vp8 and vp9 decoders ignore
/// the alpha track, and the overlay would cover the canvas with black where it should be clear.</para>
/// </summary>
public sealed record OverlayMedia(bool Loop, string? Decoder)
{
    /// <summary>One picture, shown for ever: what a file that could not be read is opened as too.</summary>
    public static readonly OverlayMedia Still = new(false, null);

    public static OverlayMedia Of(MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        // A picture read by an image demuxer is one picture: image2 for a JPEG, png_pipe, webp_pipe
        // and the like for the others. A GIF of one frame shown for a moment is a still in an
        // animated format; one ffprobe could not count is told by how long it lasts.
        var format = probe.Format ?? string.Empty;
        if (format == "image2" || format.EndsWith("_pipe", StringComparison.Ordinal)
            || (format == "gif" && probe.Frames <= 1 && probe.DurationSeconds <= 0.1))
        {
            return Still;
        }

        var decoder = !probe.AlphaMode
            ? null
            : probe.VideoCodec switch
            {
                "vp9" => "libvpx-vp9",
                "vp8" => "libvpx",
                _ => null
            };

        return new OverlayMedia(true, decoder);
    }
}
