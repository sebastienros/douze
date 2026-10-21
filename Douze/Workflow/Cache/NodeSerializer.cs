using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Cache;

/// <summary>
/// Where the serializer stores what it writes apart: large objects and arrays, and the metadata found in nodes.
/// </summary>
internal interface INodeStore
{
    /// <summary>
    /// Stores <paramref name="node"/> on its own under <paramref name="hash"/>, unless it is stored already.
    /// </summary>
    void PutNode(Node node, Hash128 hash);

    /// <summary>
    /// Stores <paramref name="metadata"/> on its own, unless it is stored already.
    /// </summary>
    void PutMetadata(Metadata metadata);
}

/// <summary>
/// Reads back what the serializer stored apart.
/// </summary>
internal interface INodeSource
{
    Node GetNode(Hash128 hash);

    Metadata GetMetadata(Hash128 hash);
}

/// <summary>
/// Writes nodes in a compact binary form that keeps the type of every value, so that a node read back hashes and
/// renders like the original. An object or an array whose serialized size exceeds <see cref="SeparateSize"/> is stored
/// apart and written as a reference to its hash: the pages of a pagination, shared by every page, are stored once, and
/// read back as one shared node.
/// </summary>
internal static class NodeSerializer
{
    /// <summary>
    /// The size above which an object or an array is stored apart.
    /// </summary>
    public const long SeparateSize = 1024;

    private enum Tag : byte
    {
        Null, String, True, False, Int, Long, Decimal, Double, Float, DateTime, DateTimeOffset, Metadata, Object, Array, Reference,
        Short, SByte, Byte, UShort, UInt, ULong, Char,
    }

    /// <summary>
    /// Writes <paramref name="node"/>; its own object or array is written inline whatever its size.
    /// </summary>
    public static void Write(BinaryWriter writer, Node? node, INodeStore store)
    {
        Write(writer, node, store, root: true);
    }

    /// <summary>
    /// Writes metadata: its directory and its own values.
    /// </summary>
    public static void WriteMetadata(BinaryWriter writer, Metadata metadata, INodeStore store)
    {
        writer.Write(metadata.Directory.FullName);
        Write(writer, metadata.Own, store, root: true);
    }

    public static (NodeObject Own, FPath Directory) ReadMetadata(BinaryReader reader, INodeSource source)
    {
        var directory = new FPath(reader.ReadString());
        var own = Read(reader, source) as NodeObject ?? throw new InvalidDataException("Metadata must be an object");
        return (own, directory);
    }

    private static void Write(BinaryWriter writer, Node? node, INodeStore store, bool root)
    {
        switch (node)
        {
            case null:
                writer.Write((byte)Tag.Null);
                return;

            case NodeObject or NodeArray when !root && NodeHash.Measure(node) is { Size: > SeparateSize } measured:
                store.PutNode(node, measured.Hash);
                writer.Write((byte)Tag.Reference);
                WriteHash(writer, measured.Hash);
                return;

            case NodeObject obj:
                writer.Write((byte)Tag.Object);
                writer.Write7BitEncodedInt(obj.Count);

                foreach (var property in obj)
                {
                    writer.Write(property.Key);
                    Write(writer, property.Value, store, root: false);
                }

                return;

            case NodeArray array:
                writer.Write((byte)Tag.Array);
                writer.Write7BitEncodedInt(array.Count);

                foreach (var item in array)
                {
                    Write(writer, item, store, root: false);
                }

                return;

            case NodeValue<Metadata> metadata:
                store.PutMetadata(metadata.Value);
                writer.Write((byte)Tag.Metadata);
                WriteHash(writer, metadata.Value.Hash);
                return;
        }

        WriteValue(writer, node);
    }

    private static void WriteValue(BinaryWriter writer, Node node)
    {
        switch (node)
        {
            case NodeValue<string> s: writer.Write((byte)Tag.String); writer.Write(s.Value); break;
            case NodeValue<bool> b: writer.Write((byte)(b.Value ? Tag.True : Tag.False)); break;
            case NodeValue<int> i: writer.Write((byte)Tag.Int); writer.Write(i.Value); break;
            case NodeValue<long> l: writer.Write((byte)Tag.Long); writer.Write(l.Value); break;
            case NodeValue<decimal> m: writer.Write((byte)Tag.Decimal); writer.Write(m.Value); break;
            case NodeValue<double> d: writer.Write((byte)Tag.Double); writer.Write(d.Value); break;
            case NodeValue<float> f: writer.Write((byte)Tag.Float); writer.Write(f.Value); break;
            case NodeValue<short> sh: writer.Write((byte)Tag.Short); writer.Write(sh.Value); break;
            case NodeValue<sbyte> sb: writer.Write((byte)Tag.SByte); writer.Write(sb.Value); break;
            case NodeValue<byte> by: writer.Write((byte)Tag.Byte); writer.Write(by.Value); break;
            case NodeValue<ushort> us: writer.Write((byte)Tag.UShort); writer.Write(us.Value); break;
            case NodeValue<uint> ui: writer.Write((byte)Tag.UInt); writer.Write(ui.Value); break;
            case NodeValue<ulong> ul: writer.Write((byte)Tag.ULong); writer.Write(ul.Value); break;
            case NodeValue<char> c: writer.Write((byte)Tag.Char); writer.Write((ushort)c.Value); break;

            case NodeValue<DateTime> dt:
                writer.Write((byte)Tag.DateTime);
                writer.Write(dt.Value.Ticks);
                writer.Write((byte)dt.Value.Kind);
                break;

            case NodeValue<DateTimeOffset> dto:
                writer.Write((byte)Tag.DateTimeOffset);
                writer.Write(dto.Value.Ticks);
                writer.Write((short)dto.Value.Offset.TotalMinutes);
                break;

            default:
                // Hashed as their text too (NodeHash).
                writer.Write((byte)Tag.String);
                writer.Write(NodeHash.Format(node));
                break;
        }
    }

    public static Node? Read(BinaryReader reader, INodeSource source)
    {
        var tag = (Tag)reader.ReadByte();

        switch (tag)
        {
            case Tag.Null: return null;
            case Tag.String: return new NodeValue<string>(reader.ReadString());
            case Tag.True: return new NodeValue<bool>(true);
            case Tag.False: return new NodeValue<bool>(false);
            case Tag.Int: return new NodeValue<int>(reader.ReadInt32());
            case Tag.Long: return new NodeValue<long>(reader.ReadInt64());
            case Tag.Decimal: return new NodeValue<decimal>(reader.ReadDecimal());
            case Tag.Double: return new NodeValue<double>(reader.ReadDouble());
            case Tag.Float: return new NodeValue<float>(reader.ReadSingle());
            case Tag.Short: return new NodeValue<short>(reader.ReadInt16());
            case Tag.SByte: return new NodeValue<sbyte>(reader.ReadSByte());
            case Tag.Byte: return new NodeValue<byte>(reader.ReadByte());
            case Tag.UShort: return new NodeValue<ushort>(reader.ReadUInt16());
            case Tag.UInt: return new NodeValue<uint>(reader.ReadUInt32());
            case Tag.ULong: return new NodeValue<ulong>(reader.ReadUInt64());
            case Tag.Char: return new NodeValue<char>((char)reader.ReadUInt16());

            case Tag.DateTime:
                var ticks = reader.ReadInt64();
                return new NodeValue<DateTime>(new DateTime(ticks, (DateTimeKind)reader.ReadByte()));

            case Tag.DateTimeOffset:
                var offsetTicks = reader.ReadInt64();
                return new NodeValue<DateTimeOffset>(new DateTimeOffset(offsetTicks, TimeSpan.FromMinutes(reader.ReadInt16())));

            case Tag.Metadata:
                return new NodeValue<Metadata>(source.GetMetadata(ReadHash(reader)));

            case Tag.Reference:
                return source.GetNode(ReadHash(reader));

            case Tag.Object:
                var count = reader.Read7BitEncodedInt();
                var obj = new NodeObject();

                for (var i = 0; i < count; i++)
                {
                    var key = reader.ReadString();
                    obj[key] = Read(reader, source);
                }

                return obj;

            case Tag.Array:
                var length = reader.Read7BitEncodedInt();
                var items = new Node?[length];

                for (var i = 0; i < length; i++)
                {
                    items[i] = Read(reader, source);
                }

                return new NodeArray(items);

            default:
                throw new InvalidDataException($"Unknown node tag {(byte)tag}");
        }
    }

    public static void WriteHash(BinaryWriter writer, Hash128 hash)
    {
        writer.Write(hash.Low);
        writer.Write(hash.High);
    }

    public static Hash128 ReadHash(BinaryReader reader)
    {
        var low = reader.ReadUInt64();
        return new Hash128(low, reader.ReadUInt64());
    }
}
