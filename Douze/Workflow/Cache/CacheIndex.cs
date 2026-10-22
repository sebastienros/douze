using System.Collections.Immutable;
using System.Text;
using Douze.FileSystems;

namespace Douze.Workflow.Cache;

internal enum BlobKind : byte { Content, Metadata, Node }

/// <summary>
/// A blob: file content by content hash, metadata by metadata hash, or a node stored apart by node hash.
/// </summary>
internal readonly record struct BlobId(BlobKind Kind, Hash128 Hash);

/// <summary>
/// Where a blob is, and the blobs it references (the nodes and metadata a metadata or node blob refers to by hash), so
/// that the blobs still in use are known without reading them.
/// </summary>
internal sealed record BlobEntry(BlobLocation Location, ImmutableArray<BlobId> Children);

/// <summary>
/// An output file of a cached unit. Its content and metadata are blobs, unless they are those of one of the unit's
/// inputs, which a replay takes from the input.
/// </summary>
internal sealed record CachedOutput(FPath Path, Hash128 Content, long Length, bool IsText, Hash128 Metadata, string? ContentType, FPath? SourcePath, DateTime? LastWriteTimeUtc);

/// <summary>
/// A site scope a reduce unit contributed: its values are a node blob, read when a unit needs them; the hashes of its
/// keys are enough to check the units that read it.
/// </summary>
internal sealed record CachedSiteScope(string Source, Hash128 Values, ImmutableArray<(string Key, Hash128 Hash)> Keys);

/// <summary>
/// The contribution of a reduce unit: its site scopes, highest precedence first, and its folder scopes, whose values
/// are node blobs.
/// </summary>
internal sealed record CachedScope(ImmutableArray<CachedSiteScope> Site, ImmutableArray<(FPath Directory, Hash128 Values)> Folders);

/// <summary>
/// The result of a unit, by result key.
/// </summary>
internal sealed record CachedUnit(string Step, string Unit, Hash128 Dependencies, ImmutableArray<CachedOutput> Outputs, CachedScope? Scope);

/// <summary>
/// The length, modification time and hash a source file had: a file with the same length and time is not read again
/// to be hashed.
/// </summary>
internal readonly record struct SourceStat(long Length, DateTime LastWriteTimeUtc, Hash128 Hash);

/// <summary>
/// The index of the disk cache: the units by result key, their dependency sets, the blobs and the packs that hold them,
/// the hashes of the source files, and the memos of steps (a value a step computed from part of a unit, by key: the
/// result of a code snippet). Stored as a binary file whose strings are written once each: a site of 32,000
/// pages has about 200,000 units.
/// </summary>
internal sealed class CacheIndex
{
    public const int FormatVersion = 2;

    private static readonly byte[] Magic = "DOUZE-CACHE\n"u8.ToArray();

    public List<string> Packs { get; } = [];

    public Dictionary<BlobId, BlobEntry> Blobs { get; } = [];

    public Dictionary<Hash128, DependencySet> Dependencies { get; } = [];

    public Dictionary<Hash128, CachedUnit> Units { get; } = [];

    public Dictionary<FPath, SourceStat> Sources { get; } = [];

    /// <summary>
    /// The memos of steps: by key, the step that stored it and the content blob that holds the value.
    /// </summary>
    public Dictionary<Hash128, (string Owner, Hash128 Value)> Memos { get; } = [];

    public void Write(Stream stream, string douzeVersion)
    {
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new List<string>();

        using var body = new MemoryStream();

        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            void String(string? value)
            {
                if (value == null)
                {
                    writer.Write7BitEncodedInt(0);
                    return;
                }

                if (!strings.TryGetValue(value, out var id))
                {
                    strings[value] = id = table.Count;
                    table.Add(value);
                }

                writer.Write7BitEncodedInt(id + 1);
            }

            void Hash(Hash128 hash) => NodeSerializer.WriteHash(writer, hash);

            void Date(DateTime? date)
            {
                writer.Write(date?.Ticks ?? -1);
                writer.Write((byte)(date?.Kind ?? DateTimeKind.Unspecified));
            }

            // 0: none; otherwise 1, plus 2 for the whole value and 4 for the count, then the members and items read.
            void Access(ValueAccess? access)
            {
                if (access == null)
                {
                    writer.Write((byte)0);
                    return;
                }

                writer.Write((byte)(1 | (access.IsWhole ? 2 : 0) | (access.CountRead ? 4 : 0)));
                writer.Write7BitEncodedInt(access.Members.Length);

                foreach (var (key, member) in access.Members)
                {
                    String(key);
                    Access(member);
                }

                writer.Write7BitEncodedInt(access.Items.Length);

                foreach (var (index, item) in access.Items)
                {
                    writer.Write7BitEncodedInt(index);
                    Access(item);
                }
            }

            writer.Write7BitEncodedInt(Packs.Count);

            foreach (var pack in Packs)
            {
                String(pack);
            }

            writer.Write7BitEncodedInt(Blobs.Count);

            foreach (var (id, entry) in Blobs)
            {
                writer.Write((byte)id.Kind);
                Hash(id.Hash);
                writer.Write7BitEncodedInt(entry.Location.Pack);
                writer.Write7BitEncodedInt64(entry.Location.Offset);
                writer.Write7BitEncodedInt(entry.Location.Length);
                writer.Write7BitEncodedInt(entry.Children.Length);

                foreach (var child in entry.Children)
                {
                    writer.Write((byte)child.Kind);
                    Hash(child.Hash);
                }
            }

            writer.Write7BitEncodedInt(Dependencies.Count);

            foreach (var (hash, set) in Dependencies)
            {
                Hash(hash);
                writer.Write7BitEncodedInt(set.Templates.Length);

                foreach (var (path, templateHash) in set.Templates)
                {
                    String(path.FullName);
                    Hash(templateHash);
                }

                writer.Write7BitEncodedInt(set.Lookups.Length);

                foreach (var lookup in set.Lookups)
                {
                    String(lookup.Directory.FullName);
                    String(lookup.Key);
                    Hash(lookup.Hash);
                    Access(lookup.Access);
                }

                writer.Write7BitEncodedInt(set.Written.Length);

                foreach (var (path, length, lastWriteTimeUtc) in set.Written)
                {
                    String(path.FullName);
                    writer.Write(length);
                    Date(lastWriteTimeUtc);
                }
            }

            writer.Write7BitEncodedInt(Units.Count);

            foreach (var (key, unit) in Units)
            {
                Hash(key);
                String(unit.Step);
                String(unit.Unit);
                Hash(unit.Dependencies);
                writer.Write7BitEncodedInt(unit.Outputs.Length);

                foreach (var output in unit.Outputs)
                {
                    String(output.Path.FullName);
                    Hash(output.Content);
                    writer.Write7BitEncodedInt64(output.Length);
                    writer.Write(output.IsText);
                    Hash(output.Metadata);
                    String(output.ContentType);
                    String(output.SourcePath?.FullName);
                    Date(output.LastWriteTimeUtc);
                }

                writer.Write(unit.Scope != null);

                if (unit.Scope is { } scope)
                {
                    writer.Write7BitEncodedInt(scope.Site.Length);

                    foreach (var site in scope.Site)
                    {
                        String(site.Source);
                        Hash(site.Values);
                        writer.Write7BitEncodedInt(site.Keys.Length);

                        foreach (var (name, keyHash) in site.Keys)
                        {
                            String(name);
                            Hash(keyHash);
                        }
                    }

                    writer.Write7BitEncodedInt(scope.Folders.Length);

                    foreach (var (directory, values) in scope.Folders)
                    {
                        String(directory.FullName);
                        Hash(values);
                    }
                }
            }

            writer.Write7BitEncodedInt(Sources.Count);

            foreach (var (path, stat) in Sources)
            {
                String(path.FullName);
                writer.Write(stat.Length);
                Date(stat.LastWriteTimeUtc);
                Hash(stat.Hash);
            }

            writer.Write7BitEncodedInt(Memos.Count);

            foreach (var (key, (owner, value)) in Memos)
            {
                Hash(key);
                String(owner);
                Hash(value);
            }
        }

        using var header = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        header.Write(Magic);
        header.Write(FormatVersion);
        header.Write(douzeVersion);
        header.Write7BitEncodedInt(table.Count);

        foreach (var value in table)
        {
            header.Write(value);
        }

        header.Flush();
        body.Position = 0;
        body.CopyTo(stream);
    }

    /// <summary>
    /// Reads an index, or returns <c>null</c> when it was written by another format or another version of Douze.
    /// </summary>
    /// <exception cref="InvalidDataException">The index is corrupt.</exception>
    public static CacheIndex? Read(Stream stream, string douzeVersion)
    {
        try
        {
            return ReadCore(stream, douzeVersion);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    private static CacheIndex? ReadCore(Stream stream, string douzeVersion)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a cache index");
        }

        if (reader.ReadInt32() != FormatVersion || reader.ReadString() != douzeVersion)
        {
            return null;
        }

        var table = new string[reader.Read7BitEncodedInt()];

        for (var i = 0; i < table.Length; i++)
        {
            table[i] = reader.ReadString();
        }

        string? String()
        {
            var id = reader.Read7BitEncodedInt();
            return id == 0 ? null : table[id - 1];
        }

        string RequiredString() => String() ?? throw new InvalidDataException("Unexpected null string");

        Hash128 Hash() => NodeSerializer.ReadHash(reader);

        ValueAccess? Access()
        {
            var flags = reader.ReadByte();

            if (flags == 0)
            {
                return null;
            }

            var members = new (string, ValueAccess)[reader.Read7BitEncodedInt()];

            for (var m = 0; m < members.Length; m++)
            {
                members[m] = (RequiredString(), Access() ?? throw new InvalidDataException("A member access is missing"));
            }

            var items = new (int, ValueAccess)[reader.Read7BitEncodedInt()];

            for (var i = 0; i < items.Length; i++)
            {
                items[i] = (reader.Read7BitEncodedInt(), Access() ?? throw new InvalidDataException("An item access is missing"));
            }

            if ((flags & 2) != 0)
            {
                return ValueAccess.Whole;
            }

            return (flags & 4) == 0 && members.Length == 0 && items.Length == 0 ? ValueAccess.Untouched : new ValueAccess(false, (flags & 4) != 0, [.. members], [.. items]);
        }

        DateTime? Date()
        {
            var ticks = reader.ReadInt64();
            var kind = (DateTimeKind)reader.ReadByte();
            return ticks < 0 ? null : new DateTime(ticks, kind);
        }

        var index = new CacheIndex();
        var packs = reader.Read7BitEncodedInt();

        for (var i = 0; i < packs; i++)
        {
            index.Packs.Add(RequiredString());
        }

        var blobs = reader.Read7BitEncodedInt();

        for (var i = 0; i < blobs; i++)
        {
            var id = new BlobId((BlobKind)reader.ReadByte(), Hash());
            var location = new BlobLocation(reader.Read7BitEncodedInt(), reader.Read7BitEncodedInt64(), reader.Read7BitEncodedInt());
            var children = new BlobId[reader.Read7BitEncodedInt()];

            for (var c = 0; c < children.Length; c++)
            {
                children[c] = new BlobId((BlobKind)reader.ReadByte(), Hash());
            }

            if (location.Pack < 0 || location.Pack >= index.Packs.Count)
            {
                throw new InvalidDataException("A blob is in an unknown pack");
            }

            index.Blobs[id] = new BlobEntry(location, [.. children]);
        }

        var sets = reader.Read7BitEncodedInt();

        for (var i = 0; i < sets; i++)
        {
            var hash = Hash();
            var templates = new (FPath, Hash128)[reader.Read7BitEncodedInt()];

            for (var t = 0; t < templates.Length; t++)
            {
                templates[t] = (new FPath(RequiredString()), Hash());
            }

            var lookups = new Lookup[reader.Read7BitEncodedInt()];

            for (var l = 0; l < lookups.Length; l++)
            {
                lookups[l] = new Lookup(new FPath(RequiredString()), RequiredString(), Hash(), Access());
            }

            var written = new (FPath, long, DateTime)[reader.Read7BitEncodedInt()];

            for (var w = 0; w < written.Length; w++)
            {
                written[w] = (new FPath(RequiredString()), reader.ReadInt64(), Date() ?? default);
            }

            index.Dependencies[hash] = new DependencySet([.. templates], [.. lookups], [.. written]);
        }

        var units = reader.Read7BitEncodedInt();

        for (var i = 0; i < units; i++)
        {
            var key = Hash();
            var step = RequiredString();
            var unit = RequiredString();
            var dependencies = Hash();
            var outputs = new CachedOutput[reader.Read7BitEncodedInt()];

            for (var o = 0; o < outputs.Length; o++)
            {
                var path = new FPath(RequiredString());
                var content = Hash();
                var length = reader.Read7BitEncodedInt64();
                var isText = reader.ReadBoolean();
                var metadata = Hash();
                var contentType = String();
                var sourcePath = String();
                var lastWrite = Date();
                outputs[o] = new CachedOutput(path, content, length, isText, metadata, contentType, sourcePath == null ? (FPath?)null : new FPath(sourcePath), lastWrite);
            }

            CachedScope? scope = null;

            if (reader.ReadBoolean())
            {
                var site = new CachedSiteScope[reader.Read7BitEncodedInt()];

                for (var s = 0; s < site.Length; s++)
                {
                    var source = RequiredString();
                    var values = Hash();
                    var keys = new (string, Hash128)[reader.Read7BitEncodedInt()];

                    for (var k = 0; k < keys.Length; k++)
                    {
                        keys[k] = (RequiredString(), Hash());
                    }

                    site[s] = new CachedSiteScope(source, values, [.. keys]);
                }

                var folders = new (FPath, Hash128)[reader.Read7BitEncodedInt()];

                for (var f = 0; f < folders.Length; f++)
                {
                    folders[f] = (new FPath(RequiredString()), Hash());
                }

                scope = new CachedScope([.. site], [.. folders]);
            }

            if (!index.Dependencies.ContainsKey(dependencies))
            {
                throw new InvalidDataException("A unit refers to an unknown dependency set");
            }

            index.Units[key] = new CachedUnit(step, unit, dependencies, [.. outputs], scope);
        }

        var sources = reader.Read7BitEncodedInt();

        for (var i = 0; i < sources; i++)
        {
            var path = new FPath(RequiredString());
            var length = reader.ReadInt64();
            var lastWrite = Date() ?? default;
            index.Sources[path] = new SourceStat(length, lastWrite, Hash());
        }

        var memos = reader.Read7BitEncodedInt();

        for (var i = 0; i < memos; i++)
        {
            var key = Hash();
            var owner = RequiredString();
            var value = Hash();

            if (!index.Blobs.ContainsKey(new BlobId(BlobKind.Content, value)))
            {
                throw new InvalidDataException("A memo refers to an unknown blob");
            }

            index.Memos[key] = (owner, value);
        }

        return index;
    }
}
