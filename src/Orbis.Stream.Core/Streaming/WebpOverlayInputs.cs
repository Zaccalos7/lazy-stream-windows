using System.Buffers.Binary;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// Session-owned copies for FFmpeg 9's native WebP loop. Only the two-byte ANIM loop count
/// changes; encoded frames, delays and alpha remain intact. Copies are removed with the session.
/// </summary>
internal sealed class WebpOverlayInputs : IDisposable
{
    private string? _directory;
    public IReadOnlyList<FfmpegCompositionItem> Items { get; private set; } = [];

    public static WebpOverlayInputs Prepare(IReadOnlyList<FfmpegCompositionItem> items)
    {
        var inputs = new WebpOverlayInputs();
        try
        {
            var copies = new Dictionary<string, string>(StringComparer.Ordinal);
            inputs.Items = items.Select(item =>
            {
                if (item.Overlay is not { Loop: true, NativeWebpLoop: true })
                {
                    return item;
                }
                if (!copies.TryGetValue(item.Target, out var path))
                {
                    path = inputs.LoopableCopy(item.Target);
                    copies.Add(item.Target, path);
                }
                return item with { Target = path };
            }).ToArray();
            return inputs;
        }
        catch
        {
            inputs.Dispose();
            throw;
        }
    }

    private string LoopableCopy(string path)
    {
        using var source = File.OpenRead(path);
        var offset = LoopCountOffset(source);
        source.Position = offset;
        Span<byte> count = stackalloc byte[2];
        source.ReadExactly(count);
        if (BinaryPrimitives.ReadUInt16LittleEndian(count) == 0)
        {
            return path;
        }

        _directory ??= Directory.CreateTempSubdirectory("orbis-webp-").FullName;
        var copy = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".webp");
        using var target = File.Create(copy);
        source.Position = 0;
        source.CopyTo(target);
        target.Position = offset;
        count.Clear();
        target.Write(count);
        return copy;
    }

    private static long LoopCountOffset(System.IO.Stream source)
    {
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("The animated WebP input has no RIFF/WEBP header.");
        }

        Span<byte> chunk = stackalloc byte[8];
        while (source.Position <= source.Length - chunk.Length)
        {
            source.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (size > source.Length - source.Position)
            {
                break;
            }
            if (chunk[..4].SequenceEqual("ANIM"u8) && size == 6)
            {
                return source.Position + 4;
            }
            source.Seek((long)size + (size & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("The animated WebP input has no valid ANIM chunk.");
    }

    public void Dispose()
    {
        if (_directory is not null)
        {
            Directory.Delete(_directory, recursive: true);
            _directory = null;
        }
    }
}
