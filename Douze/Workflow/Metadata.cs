using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// The metadata a file carries itself. Folder and site data are not copied in: they live in
/// <see cref="ScopeSet"/> and are resolved at lookup time.
/// </summary>
internal sealed class Metadata
{
    public static readonly Metadata Empty = new(new NodeObject(), FPath.Root);

    private readonly Lock _lock = new();
    private NodeObject? _own;
    private FPath _directory;
    private Func<(NodeObject Own, FPath Directory)>? _load;
    private Hash128 _hash;
    private volatile bool _hashed;

    public Metadata(NodeObject own, FPath directory)
    {
        ArgumentNullException.ThrowIfNull(own);

        if (!directory.IsDirectory)
        {
            throw new ArgumentException($"Not a directory path: '{directory}'", nameof(directory));
        }

        _own = own;
        _directory = directory;
    }

    private Metadata(Hash128 hash, Func<(NodeObject Own, FPath Directory)> load)
    {
        _hash = hash;
        _hashed = true;
        _load = load;
    }

    /// <summary>
    /// Metadata whose hash is known and whose values <paramref name="load"/> reads on first access: a file replayed from
    /// the disk cache, whose metadata most builds never read.
    /// </summary>
    public static Metadata FromLoader(Hash128 hash, Func<(NodeObject Own, FPath Directory)> load) => new(hash, load);

    /// <summary>
    /// Front matter, page values, url, pagination, captured variables. Treated as immutable once published.
    /// </summary>
    public NodeObject Own
    {
        get
        {
            EnsureLoaded();
            return _own!;
        }
    }

    /// <summary>
    /// The source directory used to find folder scopes.
    /// </summary>
    public FPath Directory
    {
        get
        {
            EnsureLoaded();
            return _directory;
        }
    }

    /// <summary>
    /// Whether <see cref="Own"/> is in memory.
    /// </summary>
    public bool IsLoaded => _load == null;

    public Hash128 Hash
    {
        get
        {
            if (!_hashed)
            {
                // Benign race: two threads compute the same value; the flag is set once the value is written.
                _hash = Hash128.Combine(NodeHash.Of(_own), Hash128.OfText(_directory.FullName));
                _hashed = true;
            }

            return _hash;
        }
    }

    /// <summary>
    /// Returns new metadata whose <see cref="Own"/> is a shallow clone of this one with <paramref name="mutate"/> applied.
    /// Nested nodes are shared, so <paramref name="mutate"/> must replace them rather than change them.
    /// </summary>
    public Metadata With(Action<NodeObject> mutate)
    {
        var own = Own.ShallowClone();
        mutate(own);
        return new Metadata(own, Directory);
    }

    public Metadata WithDirectory(FPath directory) => new(Own, directory);

    public bool TryGetOwn(string key, out Node? value) => Own.TryGetItem(key, out value);

    private void EnsureLoaded()
    {
        if (_load == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_load is { } load)
            {
                (_own, _directory) = load();
                _load = null;
            }
        }
    }
}
