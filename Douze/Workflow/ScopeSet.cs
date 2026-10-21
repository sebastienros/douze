using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Site wide values: the data folder ("data") or a step contribution ("step:&lt;name&gt;").
/// </summary>
// KeySources: Top-level key to the data file that defined it, for dependency tracking.
internal sealed record SiteScope(string Source, NodeObject Values, IReadOnlyDictionary<string, string>? KeySources = null)
{
    private Hash128? _hash;

    // 'with' expressions must not copy the cached hash.
    private SiteScope(SiteScope original)
    {
        Source = original.Source;
        Values = original.Values;
        KeySources = original.KeySources;
    }

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Source), CanonicalJson.Hash(Values));
}

/// <summary>
/// Values that apply to the files of a directory and its subdirectories.
/// </summary>
// Sources: The data files the values come from, sorted.
internal sealed record FolderScope(FPath Directory, NodeObject Values, ImmutableArray<FPath> Sources)
{
    private Hash128? _hash;

    // 'with' expressions must not copy the cached hash.
    private FolderScope(FolderScope original)
    {
        Directory = original.Directory;
        Values = original.Values;
        Sources = original.Sources;
    }

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Directory.FullName), CanonicalJson.Hash(Values));
}

internal enum ScopeKind { Own, Folder, Site, Fallback, None }

// Source: The directory of a folder scope, the data file or source of a site scope, empty otherwise.
internal readonly record struct ScopeHit(ScopeKind Kind, string Source);

/// <summary>
/// The layered, shared, read-only data a step resolves metadata against. Values are never copied into files:
/// lookups go through <see cref="TryResolve"/>, which is also where dependencies are recorded.
/// </summary>
internal sealed class ScopeSet
{
    public static readonly ScopeSet Empty = new(ImmutableDictionary<FPath, FolderScope>.Empty, []);

    private readonly ImmutableDictionary<FPath, FolderScope> _folders;
    private readonly ImmutableArray<SiteScope> _site;
    private readonly Lazy<Hash128> _hash;

    private ScopeSet(ImmutableDictionary<FPath, FolderScope> folders, ImmutableArray<SiteScope> site)
    {
        _folders = folders;
        _site = site;
        _hash = new Lazy<Hash128>(ComputeHash, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyDictionary<FPath, FolderScope> Folders => _folders;

    /// <summary>
    /// Site scopes, highest precedence first.
    /// </summary>
    public IReadOnlyList<SiteScope> Site => _site;

    /// <summary>
    /// The combined hash of every scope, used in result keys of steps that read scopes.
    /// </summary>
    public Hash128 Hash => _hash.Value;

    public bool IsEmpty => _folders.IsEmpty && _site.IsEmpty;

    /// <summary>
    /// Adds a site scope with the highest precedence.
    /// </summary>
    public ScopeSet WithSite(SiteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new ScopeSet(_folders, _site.Insert(0, scope));
    }

    /// <summary>
    /// Adds folder scopes. For a directory that already has a scope, the new top-level keys win.
    /// </summary>
    public ScopeSet WithFolders(IEnumerable<FolderScope> folders)
    {
        var builder = _folders.ToBuilder();

        foreach (var folder in folders)
        {
            if (builder.TryGetValue(folder.Directory, out var existing))
            {
                var values = existing.Values.ShallowClone();

                foreach (var property in folder.Values)
                {
                    values[property.Key] = property.Value;
                }

                var sources = existing.Sources.AddRange(folder.Sources).Distinct().Order().ToImmutableArray();
                builder[folder.Directory] = new FolderScope(folder.Directory, values, sources);
            }
            else
            {
                builder[folder.Directory] = folder;
            }
        }

        return new ScopeSet(builder.ToImmutable(), _site);
    }

    /// <summary>
    /// Layers <paramref name="contributions"/> over <paramref name="base"/>. The first contribution has the highest precedence.
    /// </summary>
    public static ScopeSet Merge(IReadOnlyList<ScopeSet> contributions, ScopeSet @base)
    {
        var result = @base;

        for (var i = contributions.Count - 1; i >= 0; i--)
        {
            var contribution = contributions[i];

            if (contribution.IsEmpty)
            {
                continue;
            }

            result = result.WithFolders(contribution._folders.Values.OrderBy(x => x.Directory));

            for (var s = contribution._site.Length - 1; s >= 0; s--)
            {
                result = result.WithSite(contribution._site[s]);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a top-level key: own metadata, then folder scopes from <c>meta.Directory</c> up to the root,
    /// then site scopes in order, then <paramref name="fallback"/>. The first scope that contains the key wins.
    /// Every scope consulted is recorded in <paramref name="deps"/>, hit or miss: a scope that does not define the
    /// key could define it later and shadow the hit, so the result depends on it too.
    /// </summary>
    public bool TryResolve(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, out Node? value, out ScopeHit hit)
    {
        deps ??= DependencyTracker.Null;

        if (meta.Own.TryGetItem(key, out value))
        {
            hit = new ScopeHit(ScopeKind.Own, "");
            return true;
        }

        if (!_folders.IsEmpty)
        {
            for (var directory = meta.Directory; !directory.IsEmpty; directory = directory.GetParentDirectory())
            {
                if (!_folders.TryGetValue(directory, out var folder))
                {
                    continue;
                }

                foreach (var source in folder.Sources)
                {
                    deps.RecordDataFile(source);
                }

                if (folder.Values.TryGetItem(key, out value))
                {
                    hit = new ScopeHit(ScopeKind.Folder, directory.FullName);
                    return true;
                }
            }
        }

        foreach (var scope in _site)
        {
            if (!scope.Values.TryGetItem(key, out value))
            {
                deps.RecordScope(scope.Source);
                continue;
            }

            if (scope.KeySources != null && scope.KeySources.TryGetValue(key, out var dataFile))
            {
                deps.RecordDataFile(dataFile);
                hit = new ScopeHit(ScopeKind.Site, dataFile);
            }
            else
            {
                deps.RecordScope(scope.Source);
                hit = new ScopeHit(ScopeKind.Site, scope.Source);
            }

            return true;
        }

        if (fallback != null && fallback.TryGetItem(key, out value))
        {
            hit = new ScopeHit(ScopeKind.Fallback, "");
            return true;
        }

        value = null;
        hit = new ScopeHit(ScopeKind.None, "");
        return false;
    }

    private Hash128 ComputeHash()
    {
        if (IsEmpty)
        {
            return Hash128.Empty;
        }

        var hashes = new List<Hash128>(_site.Length + _folders.Count);

        foreach (var scope in _site)
        {
            hashes.Add(scope.Hash);
        }

        foreach (var folder in _folders.Values.OrderBy(x => x.Directory))
        {
            hashes.Add(folder.Hash);
        }

        return Hash128.Combine([.. hashes]);
    }
}
