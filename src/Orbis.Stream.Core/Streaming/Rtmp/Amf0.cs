using System.Buffers.Binary;
using System.Text;

namespace Orbis.Stream.Core.Streaming.Rtmp;

/// <summary>
/// The AMF0 values an RTMP publisher writes (the commands of the connection) and reads (the
/// answers of the server). Only what a publish needs: numbers, booleans, strings, objects, null.
/// </summary>
internal sealed class Amf0Writer
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public Amf0Writer Number(double value)
    {
        _bytes.Add(0x00);
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public Amf0Writer Boolean(bool value)
    {
        _bytes.Add(0x01);
        _bytes.Add(value ? (byte)1 : (byte)0);
        return this;
    }

    public Amf0Writer String(string value)
    {
        _bytes.Add(0x02);
        Utf8(value);
        return this;
    }

    public Amf0Writer Null()
    {
        _bytes.Add(0x05);
        return this;
    }

    /// <summary>An anonymous object of string and number properties, in the order given.</summary>
    public Amf0Writer Object(params (string Key, object Value)[] properties)
    {
        _bytes.Add(0x03);
        foreach (var (key, value) in properties)
        {
            Utf8(key);
            switch (value)
            {
                case string text:
                    String(text);
                    break;
                case bool flag:
                    Boolean(flag);
                    break;
                default:
                    Number(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        _bytes.AddRange([0x00, 0x00, 0x09]);
        return this;
    }

    private void Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        _bytes.Add((byte)(bytes.Length >> 8));
        _bytes.Add((byte)bytes.Length);
        _bytes.AddRange(bytes);
    }
}

/// <summary>
/// Reads the values of an AMF0 message. An object comes back as a dictionary, the types a publish
/// never needs to look into (dates, references, typed objects) end the read instead of failing it:
/// the command name, its transaction and the status object always come first.
/// </summary>
internal static class Amf0Reader
{
    public static IReadOnlyList<object?> ReadAll(ReadOnlySpan<byte> data)
    {
        var values = new List<object?>();
        var offset = 0;
        while (offset < data.Length && TryRead(data, ref offset, out var value))
        {
            values.Add(value);
        }

        return values;
    }

    private static bool TryRead(ReadOnlySpan<byte> data, ref int offset, out object? value)
    {
        value = null;
        if (offset >= data.Length)
        {
            return false;
        }

        var marker = data[offset++];
        switch (marker)
        {
            case 0x00 when offset + 8 <= data.Length:
                value = BinaryPrimitives.ReadDoubleBigEndian(data[offset..]);
                offset += 8;
                return true;
            case 0x01 when offset + 1 <= data.Length:
                value = data[offset++] != 0;
                return true;
            case 0x02:
                return TryReadUtf8(data, ref offset, out value);
            case 0x05:
            case 0x06:
                return true;
            case 0x03:
                return TryReadProperties(data, ref offset, out value);
            case 0x08 when offset + 4 <= data.Length:
                // ECMA array: a count nobody trusts, then properties like an object.
                offset += 4;
                return TryReadProperties(data, ref offset, out value);
            case 0x0C when offset + 4 <= data.Length:
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
                offset += 4;
                if (offset + length > data.Length)
                {
                    return false;
                }

                value = Encoding.UTF8.GetString(data.Slice(offset, length));
                offset += length;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadUtf8(ReadOnlySpan<byte> data, ref int offset, out object? value)
    {
        value = null;
        if (offset + 2 > data.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (offset + length > data.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }

    private static bool TryReadProperties(ReadOnlySpan<byte> data, ref int offset, out object? value)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        value = properties;
        while (offset + 3 <= data.Length)
        {
            if (data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 0x09)
            {
                offset += 3;
                return true;
            }

            if (!TryReadUtf8(data, ref offset, out var key) || !TryRead(data, ref offset, out var property))
            {
                return false;
            }

            properties[(string)key!] = property;
        }

        return false;
    }
}
