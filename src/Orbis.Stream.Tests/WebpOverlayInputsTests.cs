using System.Text;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Streaming;

namespace Orbis.Stream.Tests;

public sealed class WebpOverlayInputsTests
{
    private static FfmpegCompositionItem Overlay(string path) =>
        new(SourceKind.Overlay, path, 0, 0, 64, 32, false, Overlay: new(true, null, NativeWebpLoop: true));

    private static byte[] Webp(ushort loops, bool valid = true)
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WEBP"u8);
        // Odd-length metadata must be skipped with its padding byte.
        writer.Write("JUNK"u8);
        writer.Write(3u);
        writer.Write(new byte[] { 1, 2, 3, 0 });
        writer.Write(valid ? "ANIM"u8 : "JUNK"u8);
        writer.Write(6u);
        writer.Write(0x12345678u);
        writer.Write(loops);
        writer.Write("ANMF"u8);
        writer.Write(4u);
        writer.Write(0xabcdef01u);
        data.Position = 4;
        writer.Write((uint)data.Length - 8);
        return data.ToArray();
    }

    [Fact]
    public void FiniteAnimationsGetOneOwnedCopyWithOnlyTheirRepeatCountChanged()
    {
        var original = Path.GetTempFileName();
        try
        {
            var bytes = Webp(3);
            File.WriteAllBytes(original, bytes);
            string copy;
            using (var inputs = WebpOverlayInputs.Prepare([Overlay(original), Overlay(original)]))
            {
                copy = inputs.Items[0].Target;
                Assert.NotEqual(original, copy);
                Assert.Equal(copy, inputs.Items[1].Target);
                var changed = File.ReadAllBytes(copy);
                Assert.Equal(bytes.Length, changed.Length);
                Assert.Equal(Webp(0), changed);
                Assert.Equal(bytes, File.ReadAllBytes(original));
            }
            Assert.False(File.Exists(copy));
            Assert.False(Directory.Exists(Path.GetDirectoryName(copy)));
            Assert.Equal(bytes, File.ReadAllBytes(original));
        }
        finally
        {
            File.Delete(original);
        }
    }

    [Fact]
    public void InfiniteAnimationsNeedNoCopyAndMalformedInputsAreRefused()
    {
        var original = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(original, Webp(0));
            using (var inputs = WebpOverlayInputs.Prepare([Overlay(original)]))
            {
                Assert.Equal(original, Assert.Single(inputs.Items).Target);
            }
            Assert.True(File.Exists(original));
            File.WriteAllBytes(original, Webp(1, valid: false));
            Assert.Throws<InvalidDataException>(() => WebpOverlayInputs.Prepare([Overlay(original)]));
        }
        finally
        {
            File.Delete(original);
        }
    }
}
