using System.IO.Compression;

namespace Orbis.Stream.Tests;

/// <summary>Pictures the tests decide every pixel of, without a library or ffmpeg to make them.</summary>
internal static class TestPictures
{
    /// <summary>A PNG with alpha, written by hand: the test decides every pixel of it.</summary>
    public static byte[] Png(int width, int height, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var at = row + 1 + x * 4;
                (raw[at], raw[at + 1], raw[at + 2], raw[at + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 6);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(System.IO.Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
