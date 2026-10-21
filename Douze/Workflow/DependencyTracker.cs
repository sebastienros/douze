using System.Collections.Immutable;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// Records what a unit of work read besides its input files: templates, and the keys it resolved through the scopes
/// beyond the own metadata of a file. One tracker is created per unit (a file for map, a group, or the whole input for
/// reduce).
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
    private readonly Dictionary<(FPath Directory, string Key), Hash128> _lookups = new();
    private readonly Dictionary<FPath, (long Length, DateTime LastWriteTimeUtc)> _written = new();

    public DependencyTracker() : this(enabled: true)
    {
    }

    private DependencyTracker(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Records a template file and the hash of its content, or <see cref="DependencySet.Absent"/> for a path that was
    /// looked for and does not exist (a file added there later would be used instead).
    /// </summary>
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

    /// <summary>
    /// Records that <paramref name="key"/> was resolved for a file of <paramref name="directory"/> beyond its own
    /// metadata, with what it resolves to in <paramref name="scopes"/> (<see cref="ScopeSet.ResolveHash"/>).
    /// </summary>
    public void RecordLookup(ScopeSet scopes, FPath directory, string key)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            if (_lookups.ContainsKey((directory, key)))
            {
                return;
            }
        }

        var hash = scopes.ResolveHash(directory, key);

        lock (_lock)
        {
            _lookups[(directory, key)] = hash;
        }
    }

    /// <summary>
    /// Records a file the unit wrote to the output folder, as it was once written: a result is reused only while the
    /// file is still there, unchanged.
    /// </summary>
    public void RecordWritten(FPath path, long length, DateTime lastWriteTimeUtc)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            _written[path] = (length, lastWriteTimeUtc);
        }
    }

    /// <summary>
    /// Records every dependency of <paramref name="set"/>.
    /// </summary>
    public void Add(DependencySet set)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var (path, hash) in set.Templates)
            {
                _templates[path] = hash;
            }

            foreach (var (directory, key, hash) in set.Lookups)
            {
                _lookups[(directory, key)] = hash;
            }

            foreach (var (path, length, lastWriteTimeUtc) in set.Written)
            {
                _written[path] = (length, lastWriteTimeUtc);
            }
        }
    }

    /// <summary>
    /// Returns the recorded dependencies, sorted and deduplicated.
    /// </summary>
    public DependencySet ToSet()
    {
        lock (_lock)
        {
            if (_templates.Count == 0 && _lookups.Count == 0 && _written.Count == 0)
            {
                return DependencySet.Empty;
            }

            return new DependencySet(
                [.. _templates.Select(x => (x.Key, x.Value)).OrderBy(x => x.Key)],
                [.. _lookups.Select(x => (x.Key.Directory, x.Key.Key, x.Value)).OrderBy(x => x.Directory).ThenBy(x => x.Key, StringComparer.Ordinal)],
                [.. _written.Select(x => (x.Key, x.Value.Length, x.Value.LastWriteTimeUtc)).OrderBy(x => x.Key)]);
        }
    }
}

/// <summary>
/// What a unit read besides its inputs, sorted and deduplicated, with what each dependency was: a result is reused only
/// when every template still has its hash, every lookup still resolves to the same value
/// (<see cref="ScopeSet.ResolveHash"/>), and every written file is still in the output folder as it was written.
/// </summary>
/// <param name="Templates">Layouts, includes and templates, with the hash of their content, or <see cref="Absent"/> for a path looked for that did not exist.</param>
/// <param name="Lookups">Keys resolved beyond the own metadata of a file of a directory, with the hash of what they resolved to, or <see cref="Absent"/>.</param>
/// <param name="Written">Files written to the output folder, with their length and modification time once written.</param>
internal sealed record DependencySet(
    ImmutableArray<(FPath Path, Hash128 Hash)> Templates,
    ImmutableArray<(FPath Directory, string Key, Hash128 Hash)> Lookups,
    ImmutableArray<(FPath Path, long Length, DateTime LastWriteTimeUtc)> Written)
{
    /// <summary>
    /// The hash of a dependency that does not exist: a template that is not there, a key no scope defines.
    /// </summary>
    public static readonly Hash128 Absent = default;

    public static readonly DependencySet Empty = new([], [], []);

    private Hash128? _hash;

    // 'with' expressions must not copy the cached hash.
    private DependencySet(DependencySet original)
    {
        Templates = original.Templates;
        Lookups = original.Lookups;
        Written = original.Written;
    }

    public bool IsEmpty => Templates.IsEmpty && Lookups.IsEmpty && Written.IsEmpty;

    /// <summary>
    /// A hash of every dependency, to share equal sets between units and to check a set once per step.
    /// </summary>
    public Hash128 Hash => _hash ??= ComputeHash();

    public bool Equals(DependencySet? other)
    {
        return other is not null && (ReferenceEquals(this, other) || Hash == other.Hash);
    }

    public override int GetHashCode() => Hash.GetHashCode();

    private Hash128 ComputeHash()
    {
        var hashes = new List<Hash128>(Templates.Length * 2 + Lookups.Length * 3 + Written.Length * 3 + 3);

        foreach (var (path, hash) in Templates)
        {
            hashes.Add(Hash128.OfText(path.FullName));
            hashes.Add(hash);
        }

        hashes.Add(Hash128.Empty);

        foreach (var (directory, key, hash) in Lookups)
        {
            hashes.Add(Hash128.OfText(directory.FullName));
            hashes.Add(Hash128.OfText(key));
            hashes.Add(hash);
        }

        hashes.Add(Hash128.Empty);

        foreach (var (path, length, lastWriteTimeUtc) in Written)
        {
            hashes.Add(Hash128.OfText(path.FullName));
            hashes.Add(new Hash128((ulong)length, (ulong)lastWriteTimeUtc.Ticks));
        }

        return Hash128.Combine([.. hashes]);
    }
}
