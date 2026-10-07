using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Streaming.Rtmp;

/// <summary>Why an ingest refused or dropped a publish, in the words of the server when it gave any.</summary>
public sealed class RtmpException(string message) : IOException(message);

/// <summary>
/// The RTMP side of the relay, on the sockets and the TLS of .NET: the paced FLV tags go to the
/// ingest as RTMP messages, without a second ffmpeg to copy them there.
/// <para>That is one process and one pipe less per live, and a publish that is known for what it
/// is: the ingest answers <c>NetStream.Publish.Start</c> when it takes the stream, so "on air" is
/// the server saying so rather than a byte count that only proves a socket was open. It is also
/// the last point before the wire, so the pacing of the relay is the pacing of the network.</para>
/// <para>The protocol is the subset every publisher (OBS, ffmpeg) speaks to Twitch and YouTube:
/// the plain handshake, <c>connect</c>, <c>releaseStream</c>, <c>FCPublish</c>,
/// <c>createStream</c>, <c>publish</c>, then audio, video and <c>@setDataFrame</c> messages
/// chunked at 4 KB, with the acknowledgements and pings the server asks for.</para>
/// </summary>
public sealed class RtmpPublisher : IFlvSink, IDisposable
{
    private const int HandshakeSize = 1536;
    private const int OutChunkSize = 4096;
    private const uint ExtendedTimestamp = 0xFFFFFF;
    private const int SendBuffer = 512 * 1024;

    private const int ControlChannel = 2;
    private const int CommandChannel = 3;
    private const int AudioChannel = 4;
    private const int DataChannel = 5;
    private const int VideoChannel = 6;

    private const byte SetChunkSize = 1;
    private const byte Acknowledgement = 3;
    private const byte UserControl = 4;
    private const byte WindowAckSize = 5;
    private const byte SetPeerBandwidth = 6;
    private const byte AudioMessage = 8;
    private const byte VideoMessage = 9;
    private const byte CommandAmf3 = 17;
    private const byte DataAmf0 = 18;
    private const byte CommandAmf0 = 20;

    private static readonly byte[] SetDataFrame = new Amf0Writer().String("@setDataFrame").ToArray();

    private readonly string _host;
    private readonly int _port;
    private readonly bool _secure;
    private readonly string _app;
    private readonly string _streamKey;
    private readonly string _tcUrl;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _sendTimeout;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<double, TaskCompletionSource<IReadOnlyList<object?>>> _pending = new();
    private readonly TaskCompletionSource<string> _publishStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closing = new();

    private Socket? _socket;
    private System.IO.Stream? _stream;
    private Task? _readLoop;
    private double _nextTransaction = 1;
    private int _inChunkSize = 128;
    private long _windowAckSize = 2_500_000;
    private long _bytesReceived;
    private long _bytesAcknowledged;
    private long _bytesSent;
    private int _streamId;
    private volatile bool _publishSent;
    private volatile string? _closeReason;

    public RtmpPublisher(string url, TimeSpan connectTimeout, TimeSpan sendTimeout, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Parsed by hand rather than through Uri: a stream key is the last thing a Uri should be
        // allowed to unescape or normalise.
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd > 0 ? url[..schemeEnd].ToLowerInvariant() : string.Empty;
        if (scheme is not ("rtmp" or "rtmps"))
        {
            throw new ArgumentException($"Not an RTMP url: {scheme}://", nameof(url));
        }

        var rest = url[(schemeEnd + 3)..];
        var pathStart = rest.IndexOf('/');
        var authority = pathStart < 0 ? rest : rest[..pathStart];
        var path = pathStart < 0 ? string.Empty : rest[(pathStart + 1)..];
        var keyStart = path.IndexOf('/');
        if (authority.Length == 0 || keyStart <= 0 || keyStart == path.Length - 1)
        {
            throw new ArgumentException("An RTMP url is rtmp(s)://host[:port]/app/key", nameof(url));
        }

        _secure = scheme == "rtmps";
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out var port))
        {
            _host = authority[..colon];
            _port = port;
        }
        else
        {
            _host = authority;
            _port = _secure ? 443 : 1935;
        }

        _app = path[..keyStart];
        _streamKey = path[(keyStart + 1)..];
        _tcUrl = $"{scheme}://{authority}/{_app}";
        _connectTimeout = connectTimeout;
        _sendTimeout = sendTimeout;
        _logger = logger;
    }

    /// <summary>Whether the ingest answered NetStream.Publish.Start.</summary>
    public bool IsPublishing => _publishStart.Task.IsCompletedSuccessfully;

    /// <summary>Bytes written to the connection, chunk headers included.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void Open(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        deadline.CancelAfter(_connectTimeout);
        try
        {
            OpenAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            Abort();
            throw new RtmpException(
                $"{_host} did not accept the publish within {_connectTimeout.TotalSeconds:0}s"
                + (_closeReason is { } reason ? $": {reason}" : string.Empty));
        }
        catch
        {
            Abort();
            throw;
        }
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // Every write is a whole frame at the moment it is due: Nagle would hold the small
            // ones (audio) back waiting for more, which is the delay the pacing is there to avoid.
            NoDelay = true,

            // A network that stopped taking data fails the write instead of hanging the live.
            SendTimeout = (int)_sendTimeout.TotalMilliseconds,

            // Room for a whole keyframe. A 1080p keyframe is a few hundred kilobytes, and with the
            // default 64 KB the write of the pacing thread waits on the network for the rest of it,
            // so every frame after it leaves late; here it is handed over at once and the frames
            // that follow keep their time. Under a second of the live at streaming rates, so a
            // network that stops is still noticed long before the stall timeout.
            SendBufferSize = SendBuffer
        };
        _socket = socket;
        await socket.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

        System.IO.Stream stream = new NetworkStream(socket, ownsSocket: true);
        if (_secure)
        {
            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = _host }, cancellationToken)
                .ConfigureAwait(false);
            stream = tls;
        }

        _stream = stream;
        await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("RTMP handshake done with {Host}:{Port}", _host, _port);
        _readLoop = Task.Run(() => ReadLoopAsync(stream));

        Span<byte> chunkSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(chunkSize, OutChunkSize);
        WriteMessage(ControlChannel, SetChunkSize, 0, 0, chunkSize, chunkSize: 128);

        var connected = await CommandAsync(
                CommandChannel,
                0,
                "connect",
                writer => writer.Object(
                    ("app", _app),
                    ("type", "nonprivate"),
                    ("flashVer", "FMLE/3.0 (compatible; FMSc/1.0)"),
                    ("tcUrl", _tcUrl)),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowOnError("connect", connected);
        _logger.LogDebug("RTMP connected to {Host}, app {App}", _host, _app);

        SendCommand(CommandChannel, 0, "releaseStream", writer => writer.Null().String(_streamKey));
        SendCommand(CommandChannel, 0, "FCPublish", writer => writer.Null().String(_streamKey));
        var created = await CommandAsync(CommandChannel, 0, "createStream", writer => writer.Null(), cancellationToken)
            .ConfigureAwait(false);
        ThrowOnError("createStream", created);
        _streamId = created.Count > 3 && created[3] is double id ? (int)id : 1;
        _logger.LogDebug("RTMP stream {StreamId} created on {Host}, publishing", _streamId, _host);

        _publishSent = true;
        SendCommand(DataChannel, _streamId, "publish", writer => writer.Null().String(_streamKey).String("live"));
        await _publishStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("RTMP publish accepted by {Host}", _host);
    }

    /// <summary>
    /// The plain handshake: C0 C1, S0 S1, C2 as the echo of S1, S2. The digest handshake of Flash
    /// players is for playback; every ingest takes the plain one from a publisher.
    /// </summary>
    private static async Task HandshakeAsync(System.IO.Stream stream, CancellationToken cancellationToken)
    {
        var c0c1 = new byte[1 + HandshakeSize];
        c0c1[0] = 3;
        RandomNumberGenerator.Fill(c0c1.AsSpan(9));
        await stream.WriteAsync(c0c1, cancellationToken).ConfigureAwait(false);

        var s0s1 = new byte[1 + HandshakeSize];
        await stream.ReadExactlyAsync(s0s1, cancellationToken).ConfigureAwait(false);
        if (s0s1[0] != 3)
        {
            throw new RtmpException($"Unexpected RTMP version {s0s1[0]} from the ingest");
        }

        await stream.WriteAsync(s0s1.AsMemory(1), cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(new byte[HandshakeSize], cancellationToken).ConfigureAwait(false);
    }

    public void WriteHeader(ReadOnlySpan<byte> header)
    {
        // RTMP carries the tags, not the file around them.
    }

    public void WriteTag(FlvTag tag)
    {
        if (_closeReason is { } reason)
        {
            throw new RtmpException(reason);
        }

        switch (tag.Type)
        {
            case AudioMessage:
                WriteMessage(AudioChannel, AudioMessage, _streamId, tag.Timestamp, tag.Payload);
                break;
            case VideoMessage:
                WriteMessage(VideoChannel, VideoMessage, _streamId, tag.Timestamp, tag.Payload);
                break;
            case DataAmf0:
                // The metadata of the FLV is onMetaData and its values; a publisher hands it to the
                // server as @setDataFrame so the server keeps it for whoever joins the live later.
                var payload = tag.Payload;
                var buffer = ArrayPool<byte>.Shared.Rent(SetDataFrame.Length + payload.Length);
                try
                {
                    SetDataFrame.CopyTo(buffer, 0);
                    payload.CopyTo(buffer.AsSpan(SetDataFrame.Length));
                    WriteMessage(DataChannel, DataAmf0, _streamId, 0, buffer.AsSpan(0, SetDataFrame.Length + payload.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                break;
        }
    }

    /// <summary>Unpublishes and closes, the way OBS ends a live, so the platform ends it at once.</summary>
    public void Complete()
    {
        if (_closeReason is null && IsPublishing)
        {
            try
            {
                SendCommand(CommandChannel, 0, "FCUnpublish", writer => writer.Null().String(_streamKey));
                SendCommand(CommandChannel, 0, "deleteStream", writer => writer.Null().Number(_streamId));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                // The connection is going away anyway.
            }
        }

        Abort();
    }

    public void Abort()
    {
        _closeReason ??= "The RTMP connection was closed";
        try
        {
            _closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            // Disposing the stream closes the socket under a write blocked on a full send buffer.
            _stream?.Dispose();
            _socket?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => Abort();

    private async Task<IReadOnlyList<object?>> CommandAsync(
        int channel, int streamId, string name, Action<Amf0Writer> arguments, CancellationToken cancellationToken)
    {
        var transaction = _nextTransaction++;
        var answer = new TaskCompletionSource<IReadOnlyList<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[transaction] = answer;
        Send(channel, streamId, name, transaction, arguments);
        return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SendCommand(int channel, int streamId, string name, Action<Amf0Writer> arguments) =>
        Send(channel, streamId, name, _nextTransaction++, arguments);

    private void Send(int channel, int streamId, string name, double transaction, Action<Amf0Writer> arguments)
    {
        var writer = new Amf0Writer().String(name).Number(transaction);
        arguments(writer);
        WriteMessage(channel, CommandAmf0, streamId, 0, writer.ToArray());
    }

    private void ThrowOnError(string command, IReadOnlyList<object?> answer)
    {
        if (answer.Count > 0 && answer[0] is "_error")
        {
            throw new RtmpException($"{_host} refused {command}: {Describe(answer.Count > 3 ? answer[3] : null)}");
        }
    }

    /// <summary>
    /// One message, cut in chunks: a type 0 header with everything, then type 3 headers that only
    /// repeat the channel. A timestamp past 24 bits (a live past four hours and a half) goes in the
    /// extended field, which every chunk of the message then carries, as librtmp and ffmpeg do.
    /// The whole message is one write, so a frame leaves in one piece at the moment it is due.
    /// </summary>
    private void WriteMessage(
        int channel, byte type, int streamId, long timestamp, ReadOnlySpan<byte> payload, int chunkSize = OutChunkSize)
    {
        var extended = timestamp >= ExtendedTimestamp;
        var stamp = (uint)(timestamp & 0xFFFFFFFF);
        var chunks = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
        var size = 12 + (extended ? 4 : 0) + (chunks - 1) * (1 + (extended ? 4 : 0)) + payload.Length;

        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var span = buffer.AsSpan();
            span[0] = (byte)(channel & 0x3F);
            WriteUInt24(span[1..], extended ? ExtendedTimestamp : stamp);
            WriteUInt24(span[4..], (uint)payload.Length);
            span[7] = type;
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], streamId);
            var at = 12;
            if (extended)
            {
                BinaryPrimitives.WriteUInt32BigEndian(span[at..], stamp);
                at += 4;
            }

            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                if (offset > 0)
                {
                    span[at++] = (byte)(0xC0 | (channel & 0x3F));
                    if (extended)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(span[at..], stamp);
                        at += 4;
                    }
                }

                var length = Math.Min(chunkSize, payload.Length - offset);
                payload.Slice(offset, length).CopyTo(span[at..]);
                at += length;
            }

            var stream = _stream ?? throw new RtmpException("The RTMP connection is not open");
            lock (_writeLock)
            {
                stream.Write(buffer, 0, at);
            }

            Interlocked.Add(ref _bytesSent, at);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// What the server sends back: chunked messages on their own channels. Only a handful matter
    /// to a publisher - the answers to its commands, the status of the publish, the chunk size,
    /// the acknowledgement window and the pings - and the rest is read and dropped.
    /// </summary>
    private async Task ReadLoopAsync(System.IO.Stream stream)
    {
        var channels = new Dictionary<int, InboundChannel>();
        var header = new byte[16];
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                await ReadAsync(stream, header.AsMemory(0, 1)).ConfigureAwait(false);
                var format = header[0] >> 6;
                var channelId = header[0] & 0x3F;
                if (channelId == 0)
                {
                    await ReadAsync(stream, header.AsMemory(1, 1)).ConfigureAwait(false);
                    channelId = 64 + header[1];
                }
                else if (channelId == 1)
                {
                    await ReadAsync(stream, header.AsMemory(1, 2)).ConfigureAwait(false);
                    channelId = 64 + header[1] + header[2] * 256;
                }

                if (!channels.TryGetValue(channelId, out var channel))
                {
                    channels[channelId] = channel = new InboundChannel();
                }

                var headerLength = format switch { 0 => 11, 1 => 7, 2 => 3, _ => 0 };
                if (headerLength > 0)
                {
                    await ReadAsync(stream, header.AsMemory(0, headerLength)).ConfigureAwait(false);
                    channel.Extended = ReadUInt24(header) == ExtendedTimestamp;
                    if (format <= 1)
                    {
                        channel.Length = (int)ReadUInt24(header.AsSpan(3));
                        channel.Type = header[6];
                    }
                }

                if (channel.Extended)
                {
                    await ReadAsync(stream, header.AsMemory(0, 4)).ConfigureAwait(false);
                }

                channel.Payload ??= new byte[channel.Length];
                var count = Math.Min(_inChunkSize, channel.Length - channel.Received);
                await ReadAsync(stream, channel.Payload.AsMemory(channel.Received, count)).ConfigureAwait(false);
                channel.Received += count;

                if (channel.Received >= channel.Length)
                {
                    var payload = channel.Payload;
                    channel.Payload = null;
                    channel.Received = 0;
                    Handle(channel.Type, payload);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException
            or OperationCanceledException or InvalidOperationException)
        {
            // Twitch and YouTube answer a key they do not know by hanging up on the publish: that is
            // the one thing worth telling the user, rather than that a connection was closed.
            Close(_closing.IsCancellationRequested
                ? null
                : _publishSent && !IsPublishing
                    ? $"{_host} refused the publish and closed the connection: check the stream key"
                    : $"{_host} closed the RTMP connection");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unreadable RTMP message from {Host}", _host);
            Close($"{_host} sent an unreadable RTMP message");
        }
    }

    private async Task ReadAsync(System.IO.Stream stream, Memory<byte> buffer)
    {
        await stream.ReadExactlyAsync(buffer, _closing.Token).ConfigureAwait(false);
        _bytesReceived += buffer.Length;

        // The server stops sending (and some stop taking) once a window of bytes goes unacknowledged.
        if (_windowAckSize > 0 && _bytesReceived - _bytesAcknowledged >= _windowAckSize)
        {
            _bytesAcknowledged = _bytesReceived;
            Span<byte> ack = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ack, (uint)_bytesReceived);
            WriteMessage(ControlChannel, Acknowledgement, 0, 0, ack);
        }
    }

    private void Handle(byte type, byte[] payload)
    {
        switch (type)
        {
            case SetChunkSize when payload.Length >= 4:
                _inChunkSize = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF);
                break;
            case WindowAckSize when payload.Length >= 4:
                _windowAckSize = BinaryPrimitives.ReadUInt32BigEndian(payload);
                break;
            case SetPeerBandwidth when payload.Length >= 4:
                // Answered with the same window, as librtmp does: some servers wait for it.
                WriteMessage(ControlChannel, WindowAckSize, 0, 0, payload.AsSpan(0, 4));
                break;
            case UserControl when payload.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(payload) == 6:
                // Ping request: the pong carries the same timestamp back.
                Span<byte> pong = stackalloc byte[6];
                BinaryPrimitives.WriteUInt16BigEndian(pong, 7);
                payload.AsSpan(2, 4).CopyTo(pong[2..]);
                WriteMessage(ControlChannel, UserControl, 0, 0, pong);
                break;
            case CommandAmf0:
                HandleCommand(Amf0Reader.ReadAll(payload));
                break;
            case CommandAmf3 when payload.Length > 1:
                HandleCommand(Amf0Reader.ReadAll(payload.AsSpan(1)));
                break;
        }
    }

    private void HandleCommand(IReadOnlyList<object?> values)
    {
        if (values.Count < 2 || values[0] is not string name)
        {
            return;
        }

        if (name is "_result" or "_error")
        {
            if (values[1] is double transaction && _pending.TryRemove(transaction, out var answer))
            {
                answer.TrySetResult(values);
            }

            return;
        }

        if (name != "onStatus" || values.Count < 4 || values[3] is not IReadOnlyDictionary<string, object?> info)
        {
            return;
        }

        var code = info.TryGetValue("code", out var value) ? value as string : null;
        var level = info.TryGetValue("level", out var levelValue) ? levelValue as string : null;
        _logger.LogInformation("RTMP status from {Host}: {Code} ({Level})", _host, code, level);

        if (code == "NetStream.Publish.Start")
        {
            _publishStart.TrySetResult(code);
        }
        else if (level == "error")
        {
            var reason = $"{_host}: {code} {Describe(info)}".Trim();
            _closeReason ??= reason;
            _publishStart.TrySetException(new RtmpException(reason));
        }
    }

    private void Close(string? reason)
    {
        if (reason is not null)
        {
            _closeReason ??= reason;
        }

        var failure = new RtmpException(_closeReason ?? "The RTMP connection was closed");
        _publishStart.TrySetException(failure);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(failure);
        }

        _pending.Clear();
    }

    private static string Describe(object? info) =>
        info is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue("description", out var description)
            ? description as string ?? string.Empty
            : string.Empty;

    private static void WriteUInt24(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 16);
        target[1] = (byte)(value >> 8);
        target[2] = (byte)value;
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> source) =>
        (uint)((source[0] << 16) | (source[1] << 8) | source[2]);

    /// <summary>The header of the message being received on one channel, kept for the chunks that omit it.</summary>
    private sealed class InboundChannel
    {
        public int Length;
        public byte Type;
        public bool Extended;
        public byte[]? Payload;
        public int Received;
    }
}
