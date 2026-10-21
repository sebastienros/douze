using Douze.Workflow;

namespace Douze.Nodes;

/// <summary>
/// The cached hash of an object or array node and the approximate size of its serialized form (see
/// <see cref="NodeHash"/>). Safe to read and set from several threads: they compute the same value.
/// </summary>
internal struct NodeHashCache
{
    private Hash128 _hash;
    private long _size;
    private int _state;

    public readonly bool TryGet(out Hash128 hash, out long size)
    {
        if (Volatile.Read(in _state) == 1)
        {
            hash = _hash;
            size = _size;
            return true;
        }

        hash = default;
        size = 0;
        return false;
    }

    public void Set(Hash128 hash, long size)
    {
        _hash = hash;
        _size = size;
        Volatile.Write(ref _state, 1);
    }

    public void Reset() => _state = 0;
}
