using System.Collections.Immutable;
using System.IO.Hashing;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// What a template read of a value it resolved through the scopes: the whole value, or the number of items and some
/// members and items, each with what was read of it. A unit depends on what it read only: a nav that lists the first 50
/// items of <c>collections.all</c> depends on their number and on those items, not on the other pages.
/// </summary>
/// <remarks>
/// <see cref="Project"/> hashes what was read of a node; a result is reused while the projection of the value the key
/// resolves to now is the same. A value nothing was read from projects to its kind (object, array).
/// </remarks>
internal sealed class ValueAccess
{
    public static readonly ValueAccess Whole = new(whole: true, count: false, [], []);

    public static readonly ValueAccess Untouched = new(whole: false, count: false, [], []);

    private HashSlot _shape;

    public ValueAccess(bool whole, bool count, ImmutableArray<(string Key, ValueAccess Access)> members, ImmutableArray<(int Index, ValueAccess Access)> items)
    {
        IsWhole = whole;
        CountRead = count;
        Members = members;
        Items = items;
    }

    public bool IsWhole { get; }

    /// <summary>
    /// Whether the number of members or items was read (a loop, <c>size</c>, <c>last</c>).
    /// </summary>
    public bool CountRead { get; }

    /// <summary>
    /// The members read, by key (ordinal order), present or not.
    /// </summary>
    public ImmutableArray<(string Key, ValueAccess Access)> Members { get; }

    /// <summary>
    /// The items read, by index (ascending), present or not.
    /// </summary>
    public ImmutableArray<(int Index, ValueAccess Access)> Items { get; }

    /// <summary>
    /// A hash of what was read, whatever the values: two lookups that read different parts are different dependencies.
    /// </summary>
    public Hash128 Shape => _shape.Get(this, static x => x.ComputeShape());

    /// <summary>
    /// The hash of what was read of <paramref name="node"/>.
    /// </summary>
    public Hash128 Project(Node? node)
    {
        if (IsWhole)
        {
            return NodeHash.Of(node);
        }

        var hash = new XxHash128();

        void Append(Hash128 value)
        {
            Span<byte> buffer = stackalloc byte[16];
            value.WriteTo(buffer);
            hash.Append(buffer);
        }

        switch (node)
        {
            case NodeObject obj:
                hash.Append("o"u8);

                if (CountRead)
                {
                    Append(new Hash128((ulong)obj.Count, 0));
                }

                foreach (var (key, access) in Members)
                {
                    Hash128.AppendText(hash, key);
                    hash.Append([0]);
                    Append(obj.TryGetItem(key, out var value) ? access.Project(value) : DependencySet.Absent);
                }

                break;

            case NodeArray array:
                hash.Append("a"u8);

                if (CountRead)
                {
                    Append(new Hash128((ulong)array.Count, 0));
                }

                foreach (var (index, access) in Items)
                {
                    Append(new Hash128((ulong)index, 1));
                    Append(index >= 0 && index < array.Count ? access.Project(array[index]) : DependencySet.Absent);
                }

                break;

            default:
                // A value of another kind than the one read: its own hash, which differs from any projection.
                return NodeHash.Of(node);
        }

        return Hash128.FromUInt128(hash.GetCurrentHashAsUInt128());
    }

    private Hash128 ComputeShape()
    {
        var hash = new XxHash128();
        Span<byte> buffer = stackalloc byte[16];

        buffer[0] = (byte)((IsWhole ? 1 : 0) | (CountRead ? 2 : 0));
        hash.Append(buffer[..1]);

        foreach (var (key, access) in Members)
        {
            Hash128.AppendText(hash, key);
            buffer[0] = 0;
            hash.Append(buffer[..1]);
            access.Shape.WriteTo(buffer);
            hash.Append(buffer);
        }

        buffer[0] = 1;
        hash.Append(buffer[..1]);

        foreach (var (index, access) in Items)
        {
            BitConverter.TryWriteBytes(buffer, index);
            hash.Append(buffer[..4]);
            access.Shape.WriteTo(buffer);
            hash.Append(buffer);
        }

        return Hash128.FromUInt128(hash.GetCurrentHashAsUInt128());
    }
}

/// <summary>
/// Records what a template reads of a value, while it renders (<see cref="ValueAccess"/>). Shared by the Fluid values
/// of a value and of its members and items; thread safe.
/// </summary>
/// <remarks>
/// The record is bounded: an object or an array of which more than <see cref="MaxEntries"/> members or items are read
/// (a sidebar over every package), or a value of which more than <see cref="MaxNodes"/> parts are read in all, is
/// recorded as read whole. Its hash is cached on the node, while hashing what was read would cost as much as reading it
/// in every unit, and keeping it would cost memory in every result.
/// </remarks>
internal sealed class AccessRecorder
{
    public const int MaxEntries = 64;
    public const int MaxNodes = 1024;

    // Records nothing: what is read under a value read whole.
    private static readonly AccessRecorder Ignored = new(new Root(), ignored: true);

    private readonly Root _root;
    private readonly bool _ignored;
    private bool _whole;
    private bool _count;
    private Dictionary<string, AccessRecorder>? _members;
    private Dictionary<int, AccessRecorder>? _items;

    public AccessRecorder() : this(new Root(), ignored: false)
    {
    }

    private AccessRecorder(Root root, bool ignored)
    {
        _root = root;
        _ignored = ignored;
    }

    /// <summary>
    /// The whole value was read: written, enumerated, compared, converted.
    /// </summary>
    public void ReadWhole()
    {
        if (_ignored)
        {
            return;
        }

        lock (_root)
        {
            MarkWhole();
        }
    }

    public void ReadCount()
    {
        if (_ignored)
        {
            return;
        }

        lock (_root)
        {
            _count = true;
        }
    }

    /// <summary>
    /// A member was looked for, whether or not it exists; returns the recorder of what is read of it.
    /// </summary>
    public AccessRecorder Member(string key)
    {
        if (_ignored)
        {
            return Ignored;
        }

        lock (_root)
        {
            if (_whole)
            {
                return Ignored;
            }

            _members ??= new(StringComparer.Ordinal);

            if (_members.TryGetValue(key, out var member))
            {
                return member;
            }

            if (_members.Count >= MaxEntries || ++_root.Nodes > MaxNodes)
            {
                return Overflow();
            }

            _members[key] = member = new AccessRecorder(_root, ignored: false);
            return member;
        }
    }

    /// <summary>
    /// An item was read, whether or not it exists; returns the recorder of what is read of it.
    /// </summary>
    public AccessRecorder Item(int index)
    {
        if (_ignored)
        {
            return Ignored;
        }

        lock (_root)
        {
            if (_whole)
            {
                return Ignored;
            }

            _items ??= new();

            if (_items.TryGetValue(index, out var item))
            {
                return item;
            }

            if (_items.Count >= MaxEntries || ++_root.Nodes > MaxNodes)
            {
                return Overflow();
            }

            _items[index] = item = new AccessRecorder(_root, ignored: false);
            return item;
        }
    }

    public ValueAccess Snapshot()
    {
        lock (_root)
        {
            return _root.Overflowed ? ValueAccess.Whole : SnapshotCore();
        }
    }

    // Too much read: this value is read whole, or the whole lookup when it read too many parts in all.
    private AccessRecorder Overflow()
    {
        if (_root.Nodes > MaxNodes)
        {
            _root.Overflowed = true;
        }

        MarkWhole();
        return Ignored;
    }

    private void MarkWhole()
    {
        _whole = true;
        _members = null;
        _items = null;
    }

    private ValueAccess SnapshotCore()
    {
        if (_whole)
        {
            return ValueAccess.Whole;
        }

        if (!_count && _members == null && _items == null)
        {
            return ValueAccess.Untouched;
        }

        return new ValueAccess(
            whole: false,
            _count,
            _members == null ? [] : [.. _members.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value.SnapshotCore()))],
            _items == null ? [] : [.. _items.OrderBy(x => x.Key).Select(x => (x.Key, x.Value.SnapshotCore()))]);
    }

    /// <summary>
    /// The lock and the counters of a recorder and of the recorders of its parts.
    /// </summary>
    private sealed class Root
    {
        public int Nodes;
        public bool Overflowed;
    }
}
