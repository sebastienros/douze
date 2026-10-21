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

    private readonly Lazy<Hash128> _hash;

    public Metadata(NodeObject own, FPath directory)
    {
        ArgumentNullException.ThrowIfNull(own);

        if (!directory.IsDirectory)
        {
            throw new ArgumentException($"Not a directory path: '{directory}'", nameof(directory));
        }

        Own = own;
        Directory = directory;
        _hash = new Lazy<Hash128>(() => Hash128.Combine(CanonicalJson.Hash(Own), Hash128.OfText(Directory.FullName)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Front matter, page values, url, pagination, captured variables. Treated as immutable once published.
    /// </summary>
    public NodeObject Own { get; }

    /// <summary>
    /// The source directory used to find folder scopes.
    /// </summary>
    public FPath Directory { get; }

    public Hash128 Hash => _hash.Value;

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
}
