namespace Orbis.Stream.Core.Streaming;

/// <summary>The platforms whose ingest this application knows how to feed.</summary>
public enum StreamPlatform
{
    /// <summary>A custom ingest or a file: streamed the way it always was, one ffmpeg, no relay.</summary>
    Generic,

    Twitch,

    YouTube
}

/// <summary>What carries the paced stream from the relay to the ingest.</summary>
public enum RelayTransport
{
    /// <summary>
    /// RTMP spoken by .NET itself (<see cref="Rtmp.RtmpPublisher"/>): one ffmpeg per live, and the
    /// publish confirmed by the server.
    /// </summary>
    NativeRtmp,

    /// <summary>A second ffmpeg that copies the paced FLV to the ingest: the fallback.</summary>
    FfmpegSender
}

/// <summary>
/// How a live is delivered to one platform: the pacing of the relay that sits between the encoder
/// and the ingest, and what the encoder has to produce for that ingest to accept the stream.
/// <para>The two known platforms do not forgive the same things. Twitch takes whatever arrives on
/// time and tolerates a live without sound; YouTube accepts the connection and then keeps the
/// broadcast off air when the stream has no audio track, when the keyframes are irregular, or
/// when it is fed at exactly real time with nothing in its buffer to ride out a slow moment of
/// the network. So Twitch keeps the pacing of the Java version as it was, and YouTube gets a
/// head start and a deeper buffer.</para>
/// </summary>
public sealed record StreamPlatformProfile(
    StreamPlatform Platform,

    /// <summary>
    /// Whether the encoder is paced by the relay (encoder → FLV pipe → pacer → RTMP) rather than
    /// by <c>-readrate</c> inside the encoder. False is the single ffmpeg of a custom ingest.
    /// </summary>
    bool UsesRelay,

    /// <summary>
    /// How much of the stream is sent at once when the live starts: the ingest has that much in
    /// hand before the first frame is due, which is what makes a platform go on air at once
    /// instead of waiting to have buffered enough by itself.
    /// </summary>
    TimeSpan Preroll,

    /// <summary>
    /// How far ahead of what is on air the encoder may run. That is the jitter buffer of the
    /// relay: a slow frame of the encoder is absorbed by what is already queued, and the pipe
    /// holds the encoder back once the queue is full.
    /// </summary>
    TimeSpan MaxLead,

    

    /// <summary>How long the ingest has to start taking data before the live is declared failed.</summary>
    TimeSpan ConnectTimeout,

    /// <summary>How long the stream may stop advancing on air before it is treated as broken.</summary>
    TimeSpan StallTimeout,

    /// <summary>How many times in a row a broken live is reconnected before it is an error.</summary>
    int ReconnectAttempts,

    /// <summary>Whether a silent track is added when the source has no sound at all.</summary>
    bool RequiresAudio,

    /// <summary>The audio sample rate the platform recommends.</summary>
    int AudioSampleRate,

    /// <summary>What carries the paced stream to the ingest when <see cref="UsesRelay"/>.</summary>
    RelayTransport Transport,

    /// <summary>
    /// The keyframe interval in seconds, forced on the encoder when the setting does not force
    /// one itself: both platforms drop a stream whose keyframes drift past it.
    /// </summary>
    double KeyframeSeconds,

    /// <summary>
    /// Whether the video goes out at the bitrate of the setting all the time, padded with filler
    /// when the picture needs less. YouTube measures what arrives against what the stream said it
    /// would send, and an encoder that only caps its bitrate drops far under it on a still or
    /// letterboxed picture: "YouTube is not receiving enough video", buffering for the viewers.
    /// Twitch takes the variable rate as it comes.
    /// </summary>
    bool ConstantBitrate = false)
{
    /// <summary>
    /// Twitch: no head start, and a short jitter buffer so the preview stays next to what is on
    /// air. The buffer is the whole margin there is, which is why the relay wins a slow moment
    /// back by sending a little fast rather than by writing the gap off.
    /// </summary>
    public static readonly StreamPlatformProfile Twitch = new(
        StreamPlatform.Twitch,
        UsesRelay: true,
        Preroll: TimeSpan.Zero,
        MaxLead: TimeSpan.FromSeconds(1),
        ConnectTimeout: TimeSpan.FromSeconds(20),
        StallTimeout: TimeSpan.FromSeconds(15),
        ReconnectAttempts: 3,
        RequiresAudio: false,
        AudioSampleRate: 44_100,
        Transport: RelayTransport.NativeRtmp,
        KeyframeSeconds: 2);

    /// <summary>
    /// YouTube: two seconds sent at once so the ingest has a buffer before the first frame is due,
    /// a four second jitter buffer, a silent track for a source with no sound, 48 kHz audio and a
    /// longer patience before a stall or a missing ingest is called.
    /// </summary>
    public static readonly StreamPlatformProfile YouTube = new(
        StreamPlatform.YouTube,
        UsesRelay: true,
        Preroll: TimeSpan.FromSeconds(2),
        MaxLead: TimeSpan.FromSeconds(4),
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(20),
        ReconnectAttempts: 5,
        RequiresAudio: true,
        AudioSampleRate: 48_000,
        Transport: RelayTransport.NativeRtmp,
        KeyframeSeconds: 2,
        ConstantBitrate: true);

    /// <summary>A destination this application does not know: one ffmpeg, as before.</summary>
    public static readonly StreamPlatformProfile Generic = new(
        StreamPlatform.Generic,
        UsesRelay: false,
        Preroll: TimeSpan.Zero,
        MaxLead: TimeSpan.Zero,
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(30),
        ReconnectAttempts: 0,
        RequiresAudio: false,
        AudioSampleRate: 44_100,
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 0);

    /// <summary>The profile of an output url, read off its host.</summary>
    public static StreamPlatformProfile For(string? outputUrl) => StreamPlatforms.Detect(outputUrl) switch
    {
        StreamPlatform.Twitch => Twitch,
        StreamPlatform.YouTube => YouTube,
        _ => Generic
    };
}

public static class StreamPlatforms
{
    /// <summary>
    /// The host YouTube serves RTMPS on. <c>a.rtmp.youtube.com</c> is the plain RTMP host: it
    /// answers on 443 too, but with a certificate made out to <c>*.rtmps.youtube.com</c>, so a TLS
    /// client that checks the name refuses it and one that does not is talking to a host that is
    /// not meant for it.
    /// </summary>
    private const string YouTubeRtmpsHost = "rtmps.youtube.com";

    private const string YouTubeRtmpHost = "rtmp.youtube.com";

    /// <summary>The platform an ingest url belongs to, from its host; Generic when it is not known.</summary>
    public static StreamPlatform Detect(string? outputUrl)
    {
        if (!Uri.TryCreate(outputUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
        {
            return StreamPlatform.Generic;
        }

        var host = uri.Host;
        // live-video.net is the ingest network Twitch moved to (ingest.global-contribute.live-video.net).
        if (IsOrUnder(host, "twitch.tv") || IsOrUnder(host, "live-video.net"))
        {
            return StreamPlatform.Twitch;
        }

        return IsOrUnder(host, "youtube.com") ? StreamPlatform.YouTube : StreamPlatform.Generic;
    }

    /// <summary>
    /// Points a YouTube ingest at the host YouTube serves RTMPS on (see <see cref="YouTubeRtmpsHost"/>).
    /// <para>Both halves are rewritten, and the scheme is one of them: a plain
    /// <c>rtmp://a.rtmp.youtube.com</c> is turned into <c>rtmps://a.rtmps.youtube.com</c>, not only
    /// the host of an url that already said rtmps. YouTube takes ingest over TLS and nothing else,
    /// so a plain RTMP url to it cannot produce a live whatever it is paired with. What the server
    /// does with such a connection is what makes it hard to see: it can answer the handshake and
    /// the publish as it always did and leave the broadcast off air, so the live reads as connected
    /// and there is nothing to watch. A url that already says rtmps, on any other host, is left
    /// exactly as it was.</para>
    /// </summary>
    public static string NormalizeIngestUrl(string outputUrl)
    {
        ArgumentNullException.ThrowIfNull(outputUrl);

        if (!Uri.TryCreate(outputUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)
            || !IsOrUnder(uri.Host, YouTubeRtmpHost))
        {
            return outputUrl;
        }

        // a.rtmp.youtube.com -> a.rtmps.youtube.com, b.rtmp... -> b.rtmps...: the primary and the
        // backup ingest keep their letter. Only the scheme and the authority are rewritten, so the
        // app and the key are untouched.
        var host = uri.Host;
        var fixedHost = host[..^YouTubeRtmpHost.Length] + YouTubeRtmpsHost;
        var at = outputUrl.IndexOf(host, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return outputUrl;
        }

        return string.Concat("rtmps://", fixedHost, outputUrl.AsSpan(at + host.Length));
    }

    private static bool IsOrUnder(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
