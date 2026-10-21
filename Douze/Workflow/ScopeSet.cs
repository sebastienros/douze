using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Site wide values contributed by a step ("step:&lt;name&gt;"), such as the data folder or the collections.
/// </summary>
// KeySources: Top-level key to the data file that defined it (or the folder, for a key built from a folder of files),
// for dependency tracking.
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

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Source), NodeHash.Of(Values));
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

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Directory.FullName), NodeHash.Of(Values));
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
    public static readonly ScopeSet Empty = new(ImmutableDictionary<FPath, FolderScope>.Empty, [], null);

    /// <summary>
    /// The source of the settings scope (<see cref="FromSettings"/>).
    /// </summary>
    public const string SettingsSource = "settings";

    private readonly ImmutableDictionary<FPath, FolderScope> _folders;
    private readonly ImmutableArray<SiteScope> _site;
    private readonly SiteScope? _settings;
    private readonly Lazy<Hash128> _hash;

    // An object found for a key the settings also define, completed with the settings' properties. Built once per
    // object, since the same folder or site object is resolved for many files.
    private readonly ConditionalWeakTable<NodeObject, NodeObject>? _completed;

    private ScopeSet(ImmutableDictionary<FPath, FolderScope> folders, ImmutableArray<SiteScope> site, SiteScope? settings)
    {
        _folders = folders;
        _site = site;
        _settings = settings;
        _completed = settings == null ? null : new();
        _hash = new Lazy<Hash128>(ComputeHash, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The base scopes of a build: the values of the workflow file's <c>site</c> section that templates see
    /// (<c>site.url</c>), below every contribution. Unlike other scopes, the settings do not hide behind a hit: when a
    /// scope defines the same key as an object (a <c>_data/site.json</c>), its properties are layered over the
    /// settings' object, so <c>site.title</c> comes from the data file and <c>site.url</c> from the settings unless the
    /// data file defines <c>url</c> too.
    /// </summary>
    public static ScopeSet FromSettings(NodeObject values)
    {
        return values.Count == 0 ? Empty : new ScopeSet(Empty._folders, [], new SiteScope(SettingsSource, values));
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
        return new ScopeSet(_folders, _site.Insert(0, scope), _settings);
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

        return new ScopeSet(builder.ToImmutable(), _site, _settings);
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
    /// then site scopes in order, then the settings, then <paramref name="fallback"/>. The first scope that contains
    /// the key wins, except that an object hit is completed with the settings' object of the same key
    /// (<see cref="FromSettings"/>). Every scope consulted is recorded in <paramref name="deps"/>, hit or miss: a scope
    /// that does not define the key could define it later and shadow the hit, so the result depends on it too.
    /// </summary>
    public bool TryResolve(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, out Node? value, out ScopeHit hit)
    {
        deps ??= DependencyTracker.Null;

        if (TryResolveContributed(meta, key, deps, out value, out hit))
        {
            // An object hit depends on the settings: they could complete it.
            if (_settings != null && value is NodeObject found)
            {
                deps.RecordScope(_settings.Source);

                if (_settings.Values.TryGetItem(key, out var setting) && setting is NodeObject defaults)
                {
                    value = _completed!.GetValue(found, x => Complete(x, defaults));
                }
            }

            return true;
        }

        if (_settings != null)
        {
            if (_settings.Values.TryGetItem(key, out value))
            {
                deps.RecordScope(_settings.Source);
                hit = new ScopeHit(ScopeKind.Site, _settings.Source);
                return true;
            }

            deps.RecordScope(_settings.Source);
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

    private static NodeObject Complete(NodeObject found, NodeObject defaults)
    {
        var result = defaults.ShallowClone();

        foreach (var property in found)
        {
            result[property.Key] = property.Value;
        }

        return result;
    }

    private bool TryResolveContributed(Metadata meta, string key, DependencyTracker deps, out Node? value, out ScopeHit hit)
    {
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

        value = null;
        hit = new ScopeHit(ScopeKind.None, "");
        return false;
    }

    private Hash128 ComputeHash()
    {
        if (IsEmpty && _settings == null)
        {
            return Hash128.Empty;
        }

        var hashes = new List<Hash128>(_site.Length + _folders.Count + 1);

        if (_settings != null)
        {
            hashes.Add(_settings.Hash);
        }

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
