namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// A region of the ingest of X: where a source of Media Studio Producer is made, and the server it
/// is fed on there.
/// </summary>
/// <param name="Code">The name of the server of the region: <c>de</c> for <c>de.pscp.tv</c>.</param>
/// <param name="Key">The key of the name of the region in the interface bundles.</param>
public sealed record XIngestRegion(string Code, string Key)
{
    /// <summary>The ingest server of the region.</summary>
    public string Host => Code + "." + StreamPlatforms.XIngestDomain;

    /// <summary>
    /// The address a configuration of X in this region publishes to: the RTMPS one, on 443, which
    /// is what X asks its sources to be fed with in production.
    /// </summary>
    public string StreamUrl => "rtmps://" + Host + ":443/x";
}

/// <summary>
/// The regions X takes its sources in. A source belongs to the region it was made in for good, so
/// the region is the one thing a configuration of X has to say about its address: the channel
/// settings ask for it instead of an address, and keep the address of its server.
/// </summary>
public static class XIngestRegions
{
    /// <summary>
    /// The twelve regions, in the order X lists them. California and Oregon are named the way X
    /// names them, although each of the two servers answers from the AWS region of the other: the
    /// name is what a source is picked by in Media Studio Producer.
    /// </summary>
    public static IReadOnlyList<XIngestRegion> All { get; } =
    [
        new("ca", "xRegionCa"),
        new("or", "xRegionOr"),
        new("va", "xRegionVa"),
        new("br", "xRegionBr"),
        new("fr", "xRegionFr"),
        new("ie", "xRegionIe"),
        new("de", "xRegionDe"),
        new("au", "xRegionAu"),
        new("in", "xRegionIn"),
        new("jp", "xRegionJp"),
        new("kr", "xRegionKr"),
        new("sg", "xRegionSg")
    ];

    /// <summary>The region a new configuration of X starts on: the one of the preset of the channel settings.</summary>
    public static XIngestRegion Default { get; } = All.First(region => region.Code == "de");

    /// <summary>The region of a code, whatever its case; null for a code X has no server for.</summary>
    public static XIngestRegion? Of(string? code)
    {
        var named = code?.Trim();
        return All.FirstOrDefault(region => string.Equals(region.Code, named, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The region an ingest of X is in, read off its host (<c>va.pscp.tv</c>, over RTMP or RTMPS);
    /// null for any other address.
    /// </summary>
    public static XIngestRegion? OfUrl(string? streamUrl)
    {
        if (!Uri.TryCreate(streamUrl?.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = "." + StreamPlatforms.XIngestDomain;
        return uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? Of(uri.Host[..^suffix.Length])
            : null;
    }
}
