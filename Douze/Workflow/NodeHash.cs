using System.Buffers.Binary;
using System.IO.Hashing;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Hashes nodes as a Merkle tree: an object or an array is hashed from the hashes of its items, and the hash of every
/// object and array is cached on the node. A subtree shared by many nodes, such as the pages of a pagination shared by
/// every page, is therefore hashed once, whatever the number of nodes that hold it.
/// </summary>
/// <remarks>
/// Values are hashed with their type: <c>1</c> as an integer and <c>1</c> as a decimal differ. Values of a type that is
/// not listed are hashed as the string they format to, which is how the cache stores them.
/// </remarks>
internal static class NodeHash
{
    private static readonly Hash128 Null = Hash128.OfText("null");

    public static Hash128 Of(Node? node) => Measure(node).Hash;

    /// <summary>
    /// The hash of <paramref name="node"/> and the approximate size of its serialized form, in bytes.
    /// </summary>
    public static (Hash128 Hash, long Size) Measure(Node? node)
    {
        switch (node)
        {
            case null:
                return (Null, 1);

            case NodeObject obj:
                if (obj.HashCache.TryGet(out var objectHash, out var objectSize))
                {
                    return (objectHash, objectSize);
                }

                (objectHash, objectSize) = MeasureObject(obj);
                obj.HashCache.Set(objectHash, objectSize);
                return (objectHash, objectSize);

            case NodeArray array:
                if (array.HashCache.TryGet(out var arrayHash, out var arraySize))
                {
                    return (arrayHash, arraySize);
                }

                (arrayHash, arraySize) = MeasureArray(array);
                array.HashCache.Set(arrayHash, arraySize);
                return (arrayHash, arraySize);

            default:
                return MeasureValue(node);
        }
    }

    private static (Hash128, long) MeasureObject(NodeObject obj)
    {
        var properties = obj.ToArray();
        Array.Sort(properties, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var hash = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];
        long size = 1;

        hash.Append("o"u8);

        foreach (var property in properties)
        {
            Hash128.AppendText(hash, property.Key);
            hash.Append([0]);

            var (child, childSize) = Measure(property.Value);
            child.WriteTo(buffer);
            hash.Append(buffer);
            size += property.Key.Length + childSize + 2;
        }

        return (Hash128.FromUInt128(hash.GetCurrentHashAsUInt128()), size);
    }

    private static (Hash128, long) MeasureArray(NodeArray array)
    {
        var hash = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];
        long size = 1;

        hash.Append("a"u8);

        foreach (var item in array)
        {
            var (child, childSize) = Measure(item);
            child.WriteTo(buffer);
            hash.Append(buffer);
            size += childSize;
        }

        return (Hash128.FromUInt128(hash.GetCurrentHashAsUInt128()), size);
    }

    private static (Hash128, long) MeasureValue(Node node)
    {
        if (node is NodeValue<string> s)
        {
            return (Text('s', s.Value), s.Value.Length + 2);
        }

        if (node is NodeValue<Metadata> metadata)
        {
            Span<byte> bytes = stackalloc byte[17];
            bytes[0] = (byte)'m';
            metadata.Value.Hash.WriteTo(bytes[1..]);
            return (Hash128.Of(bytes), 17);
        }

        Span<byte> buffer = stackalloc byte[17];
        var length = 9;

        switch (node)
        {
            case NodeValue<bool> b: buffer[0] = (byte)'b'; buffer[1] = b.Value ? (byte)1 : (byte)0; length = 2; break;
            case NodeValue<int> i: buffer[0] = (byte)'i'; BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], i.Value); break;
            case NodeValue<long> l: buffer[0] = (byte)'l'; BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], l.Value); break;
            case NodeValue<short> sh: buffer[0] = (byte)'h'; BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], sh.Value); break;
            case NodeValue<sbyte> sb: buffer[0] = (byte)'y'; BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], sb.Value); break;
            case NodeValue<byte> by: buffer[0] = (byte)'Y'; BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], by.Value); break;
            case NodeValue<ushort> us: buffer[0] = (byte)'H'; BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], us.Value); break;
            case NodeValue<uint> ui: buffer[0] = (byte)'I'; BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], ui.Value); break;
            case NodeValue<ulong> ul: buffer[0] = (byte)'L'; BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], ul.Value); break;
            case NodeValue<char> c: buffer[0] = (byte)'c'; BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], c.Value); break;
            case NodeValue<double> d: buffer[0] = (byte)'d'; BinaryPrimitives.WriteDoubleLittleEndian(buffer[1..], d.Value); break;
            case NodeValue<float> f: buffer[0] = (byte)'f'; BinaryPrimitives.WriteSingleLittleEndian(buffer[1..], f.Value); length = 5; break;

            case NodeValue<decimal> m:
                buffer[0] = (byte)'M';
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(m.Value, bits);

                for (var b = 0; b < 4; b++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer[(1 + b * 4)..], bits[b]);
                }

                length = 17;
                break;

            case NodeValue<DateTime> dt:
                buffer[0] = (byte)'t';
                BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], dt.Value.Ticks);
                buffer[9] = (byte)dt.Value.Kind;
                length = 10;
                break;

            case NodeValue<DateTimeOffset> dto:
                buffer[0] = (byte)'T';
                BinaryPrimitives.WriteInt64LittleEndian(buffer[1..], dto.Value.Ticks);
                BinaryPrimitives.WriteInt64LittleEndian(buffer[9..], (long)dto.Value.Offset.TotalMinutes);
                length = 17;
                break;

            default:
                // Other values (a Guid) are stored as the string they format to: they are hashed the same way.
                var text = Format(node);
                return (Text('s', text), text.Length + 2);
        }

        return (Hash128.Of(buffer[..length]), length);
    }

    /// <summary>
    /// The text of a value of a type that is neither hashed nor stored as such.
    /// </summary>
    public static string Format(Node node)
    {
        return node is IFormattable formattable ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : node.ToString() ?? "";
    }

    private static Hash128 Text(char tag, string text)
    {
        var hash = new XxHash128();
        Span<byte> prefix = [(byte)tag];
        hash.Append(prefix);
        Hash128.AppendText(hash, text);
        return Hash128.FromUInt128(hash.GetCurrentHashAsUInt128());
    }
}
