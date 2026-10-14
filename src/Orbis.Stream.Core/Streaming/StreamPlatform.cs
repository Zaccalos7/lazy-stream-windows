namespace Orbis.Stream.Core.Streaming;

/// <summary>The platforms whose ingest this application knows how to feed.</summary>
public enum StreamPlatform
{
    /// <summary>A custom ingest or a file: streamed the way it always was, one ffmpeg, no relay.</summary>
    Generic,

    Twitch,

    YouTube,

    /// <summary>
    /// Kick, which takes its lives on Amazon IVS: the same ingest network Twitch moved to, with an
    /// endpoint of its own for every account.
    /// </summary>
    Kick,

    /// <summary>
    /// Facebook Gaming: the lives of Facebook, the gaming ones among them, on the RTMPS ingest of
    /// Facebook Live.
    /// </summary>
    Facebook,

    /// <summary>
    /// TikTok LIVE, whose stream key LIVE Producer hands out for every live (it expires), and whose
    /// viewers hold their phone upright.
    /// </summary>
    TikTok,

    /// <summary>
    /// Rumble, which takes plain RTMP only: a server and a key for every stream created on its Go
    /// Live page, or the ones of a Static Stream Key, which do not change from one live to the next.
    /// </summary>
    Rumble,

    /// <summary>
    /// X (formerly Twitter), whose lives are fed through the sources of Media Studio Producer: a
    /// persistent key per source, on one ingest server per region under the network Periscope left
    /// it, and a broadcast started on the source to go on air.
    /// </summary>
    X
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

/// <summary>Who holds the stream to real time on its way to the ingest.</summary>
public enum RelayPacing
{
    /// <summary>
    /// The relay itself: every tag is handed over at the moment its timestamp says, on the clock of
    /// <see cref="HybridWaiter"/>, and a late live is won back at twice real time. What Twitch gets.
    /// </summary>
    Relay,

    /// <summary>
    /// The ffmpeg sender: the relay only joins the encoders on one timeline and keeps them no more
    /// than <see cref="StreamPlatformProfile.MaxLead"/> ahead, and the sender reads that at real
    /// time (<c>-readrate 1</c>) with the preroll as its initial burst, then publishes it over its
    /// own RTMP(S). The pipeline OBS and every ffmpeg recipe use against YouTube, which is the
    /// ingest that judges the rhythm of what arrives most strictly.
    /// </summary>
    Sender
}

/// <summary>
/// How a live is delivered to one platform: the pacing of the relay that sits between the encoder
/// and the ingest, and what the encoder has to produce for that ingest to accept the stream.
/// <para>The known platforms do not forgive the same things. Twitch takes whatever arrives on
/// time and tolerates a live without sound; YouTube accepts the connection and then keeps the
/// broadcast off air when the stream has no audio track, when the keyframes are irregular, or
/// when it is fed at exactly real time with nothing in its buffer to ride out a slow moment of
/// the network. So Twitch keeps the pacing of the Java version as it was, and YouTube gets a
/// head start and a deeper buffer.</para>
/// <para>Those are the two deliveries there are, and they stay two: the relay keeping the time
/// over the native RTMP for a low latency player (Twitch), the ffmpeg sender keeping it with a
/// head start for a player that buffers (YouTube). Kick is an ingest of the first kind - Amazon
/// IVS, the network the Twitch ingest itself runs on - and Facebook Gaming, TikTok LIVE, Rumble and
/// X of the second, so each of them is the delivery of its kind with the needs of its own platform
/// on top.</para>
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
    bool ConstantBitrate = false,

    /// <summary>Who paces the stream: the relay (Twitch) or the ffmpeg sender (YouTube).</summary>
    RelayPacing Pacing = RelayPacing.Relay,

    /// <summary>
    /// Whether every encoder of one connection produces the same format: the picture of the first
    /// one (size and rate, the others fitted into it) and stereo sound. YouTube stops making its
    /// renditions when the stream changes format halfway; Twitch follows the change, and keeps
    /// every file at its own size.
    /// </summary>
    bool UniformFormat = false,

    /// <summary>
    /// Whether the configuration of the channel chooses what publishes the live (the ffmpeg switch
    /// of the channel settings). YouTube only: Twitch always goes out the way it always did.
    /// </summary>
    bool ChoosableTransport = false,

    /// <summary>
    /// The window of the rate control of the encoder (its VBV buffer, <c>-bufsize</c>): the most a
    /// single frame may spend above the bitrate is this much of the bitrate. A keyframe is the
    /// frame that spends it - coded on its own, it costs many times a predicted one - and every
    /// frame behind it waits on the wire for as long as it takes to send, which is the peak a
    /// low latency chain has to keep small (the Gradual Decoder Refresh of VVC exists to remove
    /// exactly that peak). The platforms cut their segments on keyframes, so they cannot be spread
    /// over many frames the way GDR spreads them; what is left is to bound them, and one second
    /// is the bound every streaming encoder uses. Zero is the two seconds a custom ingest had.
    /// </summary>
    TimeSpan RateBuffer = default,

    /// <summary>
    /// Whether the picture of the live stands up (9:16): every frame of the live is turned to
    /// portrait, and a landscape source is fitted into it with black above and below, the way
    /// the viewers of the platform see a film on a phone held upright (see <see cref="Orient"/>).
    /// </summary>
    bool Portrait = false,

    /// <summary>
    /// How the bitrate of the live follows what the network carries (see
    /// <see cref="BitrateLadder"/>); null keeps the bitrate of the setting whatever happens, which
    /// is what a custom ingest without a relay gets: there is nothing there to measure it on.
    /// </summary>
    RateAdaptation? Adaptation = null,

    /// <summary>
    /// Whether every frame is a keyframe or predicted from the one frame before it: no B-frames and
    /// a single reference frame, asked of every encoder whatever the setting says. Rumble asks that
    /// of a stream, and calls a stream outside its settings one that "may result in an unstable and
    /// disrupted stream and recording".
    /// </summary>
    bool SingleReference = false)
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
        KeyframeSeconds: 2,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.LowLatency);

    /// <summary>
    /// YouTube: a delivery of its own. The paced stream leaves through an ffmpeg sender that keeps
    /// the time itself (see <see cref="RelayPacing.Sender"/>) and speaks RTMPS with the stack every
    /// YouTube encoder uses, rather than through the clock and the RTMP of this application that
    /// Twitch keeps. Around it: two seconds sent at once so the ingest has a buffer before the
    /// first frame is due, a four second jitter buffer, a constant bitrate, a silent track for a
    /// source with no sound, 48 kHz audio and a longer patience before a stall or a missing ingest
    /// is called.
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
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 2,
        ConstantBitrate: true,
        Pacing: RelayPacing.Sender,
        UniformFormat: true,
        ChoosableTransport: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.Buffered);

    /// <summary>
    /// Kick: the delivery of Twitch - the relay keeps the time, over the native RTMP, which speaks
    /// RTMPS to the IVS ingest Kick runs on - and the needs of IVS on top of it. IVS takes AAC at
    /// 48 kHz in stereo, and one format for the whole publish: a player of a low latency channel
    /// that is handed another size halfway is a player that starts over. A silent track for a
    /// source with no sound costs nothing next to a channel whose player waits for one.
    /// </summary>
    public static readonly StreamPlatformProfile Kick = new(
        StreamPlatform.Kick,
        UsesRelay: true,
        Preroll: TimeSpan.Zero,
        MaxLead: TimeSpan.FromSeconds(1),
        ConnectTimeout: TimeSpan.FromSeconds(20),
        StallTimeout: TimeSpan.FromSeconds(15),
        ReconnectAttempts: 3,
        RequiresAudio: true,
        AudioSampleRate: 48_000,
        Transport: RelayTransport.NativeRtmp,
        KeyframeSeconds: 2,
        UniformFormat: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.LowLatency);

    /// <summary>
    /// Facebook Gaming: the delivery of YouTube, whose ingest it resembles in everything that
    /// matters here. Facebook takes RTMPS and nothing else, ends a broadcast that does not carry
    /// sound and picture together or that changes its settings halfway, and measures what arrives
    /// against a constant rate - so the ffmpeg sender keeps the time with a head start, over the
    /// TLS of ffmpeg, at a constant bitrate, with one format for the whole live and a silent track
    /// for a source that has none.
    /// </summary>
    public static readonly StreamPlatformProfile Facebook = new(
        StreamPlatform.Facebook,
        UsesRelay: true,
        Preroll: TimeSpan.FromSeconds(2),
        MaxLead: TimeSpan.FromSeconds(4),
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(20),
        ReconnectAttempts: 5,
        RequiresAudio: true,
        AudioSampleRate: 48_000,
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 2,
        ConstantBitrate: true,
        Pacing: RelayPacing.Sender,
        UniformFormat: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.Buffered);

    /// <summary>
    /// TikTok LIVE: the delivery of YouTube - the ffmpeg sender keeps the time with a head start,
    /// which is how every ffmpeg recipe for TikTok publishes - with a shorter jitter buffer, since
    /// a TikTok live is talked to while it runs. The picture stands up: TikTok is watched on a
    /// phone held upright, so the live goes out at 9:16 and a landscape file is fitted into it,
    /// and since every encoder of the live is fitted into the same frame the format never changes
    /// halfway either.
    /// </summary>
    public static readonly StreamPlatformProfile TikTok = new(
        StreamPlatform.TikTok,
        UsesRelay: true,
        Preroll: TimeSpan.FromSeconds(2),
        MaxLead: TimeSpan.FromSeconds(3),
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(20),
        ReconnectAttempts: 3,
        RequiresAudio: true,
        AudioSampleRate: 48_000,
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 2,
        Pacing: RelayPacing.Sender,
        UniformFormat: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Portrait: true,
        Adaptation: RateAdaptation.Buffered);

    /// <summary>
    /// Rumble: the delivery of YouTube - the ffmpeg sender keeps the time with a head start, the way
    /// the ffmpeg recipes for Rumble publish, over the plain RTMP its ingest speaks (it serves no
    /// RTMPS) - with what its settings page asks of a stream on top: a constant bitrate, a keyframe
    /// every two seconds, no B-frames and one reference frame, AAC at 44.1 kHz. One format for the
    /// whole live and a silent track for a source with no sound, like every ingest of its kind.
    /// </summary>
    public static readonly StreamPlatformProfile Rumble = new(
        StreamPlatform.Rumble,
        UsesRelay: true,
        Preroll: TimeSpan.FromSeconds(2),
        MaxLead: TimeSpan.FromSeconds(4),
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(20),
        ReconnectAttempts: 5,
        RequiresAudio: true,
        AudioSampleRate: 44_100,
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 2,
        ConstantBitrate: true,
        Pacing: RelayPacing.Sender,
        UniformFormat: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.Buffered,
        SingleReference: true);

    /// <summary>
    /// X: a delivery of its own, next to the one of YouTube. The ffmpeg sender keeps the time, over
    /// the RTMPS X asks its sources to be fed with, but with no head start: the ingest of X (the
    /// Periscope network) was seen closing a publish a second after the burst of the YouTube
    /// delivery, so the stream leaves at the rate it plays from the first frame, the way OBS sends
    /// it. The jitter buffer stays on this side of the sender. On top of it what X asks of a source:
    /// a keyframe every three seconds (90 frames at 30 fps), AAC at 44.1 kHz, one format for the
    /// whole live and a silent track for a source with no sound. Going quiet costs the
    /// broadcast on X: one whose source stops sending times out, and cannot be started again.
    /// </summary>
    public static readonly StreamPlatformProfile X = new(
        StreamPlatform.X,
        UsesRelay: true,
        Preroll: TimeSpan.Zero,
        MaxLead: TimeSpan.FromSeconds(4),
        ConnectTimeout: TimeSpan.FromSeconds(30),
        StallTimeout: TimeSpan.FromSeconds(20),
        ReconnectAttempts: 5,
        RequiresAudio: true,
        AudioSampleRate: 44_100,
        Transport: RelayTransport.FfmpegSender,
        KeyframeSeconds: 3,
        Pacing: RelayPacing.Sender,
        UniformFormat: true,
        RateBuffer: TimeSpan.FromSeconds(1),
        Adaptation: RateAdaptation.Buffered);

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
        StreamPlatform.Kick => Kick,
        StreamPlatform.Facebook => Facebook,
        StreamPlatform.TikTok => TikTok,
        StreamPlatform.Rumble => Rumble,
        StreamPlatform.X => X,
        _ => Generic
    };

    /// <summary>
    /// The frame of a live as this platform shows it: stood up on a platform watched in portrait
    /// (a 1920x1080 frame is a 1080x1920 one there, with the same pixels), as it is everywhere else.
    /// </summary>
    public MediaOutput Orient(MediaOutput frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Portrait && frame.Width > frame.Height
            ? frame with { Width = frame.Height, Height = frame.Width }
            : frame;
    }

    /// <summary>
    /// This profile carried by the transport the channel asks for, where the platform lets it
    /// choose (<see cref="ChoosableTransport"/>); anywhere else the request is ignored. The pacing
    /// goes with the transport: the ffmpeg sender keeps the time itself, and the native publisher
    /// has only the relay to keep it.
    /// </summary>
    public StreamPlatformProfile WithTransport(RelayTransport? transport) =>
        transport is { } chosen && ChoosableTransport && chosen != Transport
            ? this with
            {
                Transport = chosen,
                Pacing = chosen == RelayTransport.FfmpegSender ? RelayPacing.Sender : RelayPacing.Relay
            }
            : this;
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

    /// <summary>
    /// The global ingest of Amazon IVS. Twitch publishes on it under a name
    /// (<c>ingest.global-contribute.live-video.net</c>); an IVS channel - every Kick account - has
    /// an endpoint of its own under it, named by twelve hex digits
    /// (<c>fa723fc1b171.global-contribute.live-video.net</c>).
    /// </summary>
    private const string IvsGlobalIngest = "global-contribute.live-video.net";

    /// <summary>The length of the name of an IVS channel endpoint, in hex digits.</summary>
    private const int IvsEndpointLength = 12;

    /// <summary>
    /// The domains TikTok hands its ingests out under: LIVE Producer shows a server of its region,
    /// <c>push-rtmp-….tiktokcdn.com</c> and its regional twins, so the platform is read off the
    /// domain and never off one address.
    /// </summary>
    private static readonly string[] TikTokDomains =
        ["tiktokcdn.com", "tiktokcdn-eu.com", "tiktokcdn-us.com", "tiktokv.com", "tiktok.com"];

    /// <summary>The domain of the servers Rumble hands its streams out on (<c>ls8.live.rmbl.ws</c>).</summary>
    private const string RumbleIngestDomain = "rmbl.ws";

    /// <summary>
    /// The domain of the ingest servers of X, one per region (<c>va.pscp.tv</c>, <c>de.pscp.tv</c>,
    /// see <see cref="XIngestRegions"/>): the network of Periscope, which X kept for the sources of
    /// Media Studio Producer.
    /// </summary>
    internal const string XIngestDomain = "pscp.tv";

    /// <summary>The platform an ingest url belongs to, from its host; Generic when it is not known.</summary>
    public static StreamPlatform Detect(string? outputUrl)
    {
        if (!Uri.TryCreate(outputUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
        {
            return StreamPlatform.Generic;
        }

        var host = uri.Host;

        // Before Twitch: a Kick endpoint is under the same network as the Twitch ingest, and only
        // its name tells the two apart.
        if (IsOrUnder(host, "kick.com") || IsIvsChannelEndpoint(host))
        {
            return StreamPlatform.Kick;
        }

        // live-video.net is the ingest network Twitch moved to (ingest.global-contribute.live-video.net).
        if (IsOrUnder(host, "twitch.tv") || IsOrUnder(host, "live-video.net"))
        {
            return StreamPlatform.Twitch;
        }

        if (IsOrUnder(host, "youtube.com"))
        {
            return StreamPlatform.YouTube;
        }

        // Facebook publishes on more than one host of its own (rtmp-api, live-api-s), and the
        // Graph API hands every broadcast an address of its own: the domain is what they share.
        if (IsOrUnder(host, "facebook.com"))
        {
            return StreamPlatform.Facebook;
        }

        foreach (var domain in TikTokDomains)
        {
            if (IsOrUnder(host, domain))
            {
                return StreamPlatform.TikTok;
            }
        }

        // Rumble gives every stream created on its Go Live page a server and a slot of its own
        // (ls8.live.rmbl.ws/slot-20), and a Static Stream Key one server under its own name
        // (rtmp.rumble.com): the domains are what they share.
        if (IsOrUnder(host, RumbleIngestDomain) || IsOrUnder(host, "rumble.com"))
        {
            return StreamPlatform.Rumble;
        }

        // X ingests on one server per region (va.pscp.tv, de.pscp.tv...), every one of them under
        // the domain Periscope left it.
        if (IsOrUnder(host, XIngestDomain))
        {
            return StreamPlatform.X;
        }

        return StreamPlatform.Generic;
    }

    /// <summary>An ingest endpoint of an IVS channel: twelve hex digits under the global ingest.</summary>
    private static bool IsIvsChannelEndpoint(string host)
    {
        if (host.Length != IvsEndpointLength + 1 + IvsGlobalIngest.Length
            || !host.EndsWith("." + IvsGlobalIngest, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var character in host.AsSpan(0, IvsEndpointLength))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
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
