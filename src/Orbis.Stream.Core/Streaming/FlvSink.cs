using System.Buffers;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// One FLV tag as the encoder wrote it: the 11 byte header, the payload and the trailing
/// PreviousTagSize, in a buffer rented from the shared pool. A keyframe of a 1080p live is well
/// over the 85 KB past which .NET allocates on the large object heap, so a buffer per tag would
/// be a large allocation every couple of seconds for the whole live; rented, it is none.
/// </summary>
public sealed class FlvTag
{
    public const int HeaderSize = 11;
    public const int TrailerSize = 4;

    private byte[]? _buffer;

    internal FlvTag(byte type, long timestamp, byte[] buffer, int length)
    {
        Type = type;
        Timestamp = timestamp;
        _buffer = buffer;
        Length = length;
    }

    /// <summary>8 audio, 9 video, 18 script data.</summary>
    public byte Type { get; }

    /// <summary>Milliseconds, the 24 bits of the header and the 8 of its extension.</summary>
    public long Timestamp { get; private set; }

    public int Length { get; }

    /// <summary>The whole tag, as it is in the FLV.</summary>
    public ReadOnlySpan<byte> Bytes => Buffer.AsSpan(0, Length);

    /// <summary>What an RTMP message carries: the tag without its header and trailer.</summary>
    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(HeaderSize, Length - HeaderSize - TrailerSize);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(FlvTag));

    /// <summary>
    /// Moves the tag to another moment of the stream, in the header as well as here: a sink that
    /// writes the bytes as they are (an ffmpeg sender) has to see the same time as one that reads
    /// the property (RTMP). This is how the encoders of one live are laid end to end on one timeline.
    /// </summary>
    internal void Restamp(long timestamp)
    {
        var header = Buffer.AsSpan(4, 4);
        header[0] = (byte)(timestamp >> 16);
        header[1] = (byte)(timestamp >> 8);
        header[2] = (byte)timestamp;
        header[3] = (byte)(timestamp >> 24);
        Timestamp = timestamp;
    }

    /// <summary>Gives the buffer back to the pool; the tag is unusable after.</summary>
    public void Release()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Where the paced relay hands the stream: the RTMP connection itself, or a stream of FLV bytes
/// (the standard input of an ffmpeg sender, a file, a test).
/// </summary>
public interface IFlvSink
{
    /// <summary>Blocks until the destination takes tags: for RTMP, until the publish was accepted.</summary>
    void Open(CancellationToken cancellationToken);

    void WriteHeader(ReadOnlySpan<byte> header);

    void WriteTag(FlvTag tag);

    /// <summary>The stream is over: the destination is closed the way it expects to be.</summary>
    void Complete();

    /// <summary>The live is being torn down: closes at once and unblocks a write in progress.</summary>
    void Abort();
}

/// <summary>The FLV bytes as they came, written to a stream and flushed tag by tag.</summary>
public sealed class StreamFlvSink(System.IO.Stream destination) : IFlvSink
{
    public void Open(CancellationToken cancellationToken)
    {
    }

    public void WriteHeader(ReadOnlySpan<byte> header)
    {
        destination.Write(header);
        destination.Flush();
    }

    public void WriteTag(FlvTag tag)
    {
        destination.Write(tag.Bytes);
        destination.Flush();
    }

    /// <summary>End of input for whoever reads the stream: an ffmpeg sender closes its connection.</summary>
    public void Complete() => Abort();

    public void Abort()
    {
        try
        {
            destination.Dispose();
        }
        catch (IOException)
        {
        }
    }
}
