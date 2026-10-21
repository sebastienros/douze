using System.Collections.Immutable;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// Records what a unit of work read besides its input files: templates, data files and step contributions.
/// One tracker is created per unit (a file for map, a group, or the whole input for reduce).
/// </summary>
internal sealed class DependencyTracker
{
    /// <summary>
    /// A tracker that records nothing.
    /// </summary>
    public static readonly DependencyTracker Null = new(enabled: false);

    private readonly bool _enabled;
    private readonly Lock _lock = new();
    private readonly Dictionary<FPath, Hash128> _templates = new();
    private readonly HashSet<FPath> _dataFiles = new();
    private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);

    public DependencyTracker() : this(enabled: true)
    {
    }

    private DependencyTracker(bool enabled)
    {
        _enabled = enabled;
    }

    public void RecordTemplate(FPath path, Hash128 contentHash)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            _templates[path] = contentHash;
        }
    }

    public void RecordDataFile(FPath path)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            _dataFiles.Add(path);
        }
    }

    public void RecordScope(string source)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            _scopes.Add(source);
        }
    }

    /// <summary>
    /// Records every dependency of <paramref name="set"/>.
    /// </summary>
    public void Add(DependencySet set)
    {
        foreach (var (path, hash) in set.Templates)
        {
            RecordTemplate(path, hash);
        }

        foreach (var path in set.DataFiles)
        {
            RecordDataFile(path);
        }

        foreach (var scope in set.Scopes)
        {
            RecordScope(scope);
        }
    }

    /// <summary>
    /// Returns the recorded dependencies, sorted and deduplicated.
    /// </summary>
    public DependencySet ToSet()
    {
        lock (_lock)
        {
            if (_templates.Count == 0 && _dataFiles.Count == 0 && _scopes.Count == 0)
            {
                return DependencySet.Empty;
            }

            return new DependencySet(
                [.. _templates.Select(x => (x.Key, x.Value)).OrderBy(x => x.Key)],
                [.. _dataFiles.Order()],
                [.. _scopes.Order(StringComparer.Ordinal)]);
        }
    }
}

internal sealed record DependencySet(ImmutableArray<(FPath Path, Hash128 Hash)> Templates, ImmutableArray<FPath> DataFiles, ImmutableArray<string> Scopes)
{
    public static readonly DependencySet Empty = new([], [], []);

    private Hash128? _hash;

    // 'with' expressions must not copy the cached hash.
    private DependencySet(DependencySet original)
    {
        Templates = original.Templates;
        DataFiles = original.DataFiles;
        Scopes = original.Scopes;
    }

    public bool IsEmpty => Templates.IsEmpty && DataFiles.IsEmpty && Scopes.IsEmpty;

    public Hash128 Hash => _hash ??= ComputeHash();

    private Hash128 ComputeHash()
    {
        var hashes = new List<Hash128>(Templates.Length * 2 + 3);

        foreach (var (path, hash) in Templates)
        {
            hashes.Add(Hash128.OfText(path.FullName));
            hashes.Add(hash);
        }

        hashes.Add(Hash128.OfStrings(DataFiles.Select(x => x.FullName)));
        hashes.Add(Hash128.OfStrings(Scopes));

        return Hash128.Combine([.. hashes]);
    }
}
