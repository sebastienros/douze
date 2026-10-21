using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Douze.Workflow;

/// <summary>
/// A 128 bit XxHash128 value. Stable across runs and platforms, used for content, metadata and result keys.
/// </summary>
internal readonly record struct Hash128(ulong Low, ulong High) : IComparable<Hash128>
{
    private const int BufferSize = 16 * 1024;

    public static readonly Hash128 Empty = Of(ReadOnlySpan<byte>.Empty);

    public static Hash128 Of(ReadOnlySpan<byte> data)
    {
        return FromUInt128(XxHash128.HashToUInt128(data));
    }

    /// <summary>
    /// Hashes the UTF-8 bytes of <paramref name="text"/> without allocating a copy of the encoded text.
    /// </summary>
    public static Hash128 OfText(string text)
    {
        var hash = new XxHash128();
        AppendText(hash, text);
        return FromUInt128(hash.GetCurrentHashAsUInt128());
    }

    /// <summary>
    /// Hashes the concatenated 16-byte values, so the result depends on the order of <paramref name="hashes"/>.
    /// </summary>
    public static Hash128 Combine(params ReadOnlySpan<Hash128> hashes)
    {
        var hash = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];

        foreach (var h in hashes)
        {
            h.WriteTo(buffer);
            hash.Append(buffer);
        }

        return FromUInt128(hash.GetCurrentHashAsUInt128());
    }

    /// <summary>
    /// Hashes each string's UTF-8 bytes followed by a 0 byte.
    /// </summary>
    public static Hash128 OfStrings(IEnumerable<string> values)
    {
        var hash = new XxHash128();
        ReadOnlySpan<byte> separator = [0];

        foreach (var value in values)
        {
            AppendText(hash, value);
            hash.Append(separator);
        }

        return FromUInt128(hash.GetCurrentHashAsUInt128());
    }

    public static Hash128 Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (hex.Length != 32)
        {
            throw new FormatException($"A hash is 32 hexadecimal characters: '{hex}'");
        }

        var high = ulong.Parse(hex.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var low = ulong.Parse(hex.AsSpan(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        return new Hash128(low, high);
    }

    public int CompareTo(Hash128 other)
    {
        var result = High.CompareTo(other.High);
        return result != 0 ? result : Low.CompareTo(other.Low);
    }

    /// <summary>
    /// Writes the 16 bytes of the hash, low part first, little endian.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], High);
    }

    /// <summary>
    /// Returns 32 lowercase hexadecimal characters, high part first (XXH128's canonical representation).
    /// </summary>
    public override string ToString()
    {
        return string.Create(32, this, static (span, hash) =>
        {
            hash.High.TryFormat(span, out _, "x16", CultureInfo.InvariantCulture);
            hash.Low.TryFormat(span[16..], out _, "x16", CultureInfo.InvariantCulture);
        });
    }

    internal static void AppendText(XxHash128 hash, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            var chars = text.AsSpan();
            var completed = false;

            while (!completed)
            {
                encoder.Convert(chars, buffer, flush: true, out var charsUsed, out var bytesUsed, out completed);
                hash.Append(buffer.AsSpan(0, bytesUsed));
                chars = chars[charsUsed..];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static Hash128 FromUInt128(UInt128 value)
    {
        return new Hash128((ulong)value, (ulong)(value >> 64));
    }
}
