using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Site wide values contributed by a step ("step:&lt;name&gt;"), such as the data folder or the collections, or the
/// settings of the workflow file.
/// </summary>
internal sealed class SiteScope
{
    private readonly NodeObject? _values;
    private readonly Lazy<NodeObject>? _load;
    private readonly IReadOnlyDictionary<string, Hash128>? _keys;
    private readonly ConcurrentDictionary<string, Hash128>? _keyHashes;
    private Hash128? _hash;

    public SiteScope(string source, NodeObject values)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(values);

        Source = source;
        _values = values;
        _keyHashes = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A scope restored from the disk cache: the hashes of its keys are known, its values are read by
    /// <paramref name="load"/> when a unit reads them. Units replayed from the cache only compare hashes.
    /// </summary>
    public SiteScope(string source, Hash128 valuesHash, IReadOnlyDictionary<string, Hash128> keyHashes, Func<NodeObject> load)
    {
        Source = source;
        _keys = keyHashes;
        _load = new Lazy<NodeObject>(load, LazyThreadSafetyMode.ExecutionAndPublication);
        _hash = Hash128.Combine(Hash128.OfText(source), valuesHash);
    }

    public string Source { get; }

    public NodeObject Values => _values ?? _load!.Value;

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Source), NodeHash.Of(Values));

    public bool TryGetValue(string key, out Node? value)
    {
        if (_keys != null && !_keys.ContainsKey(key))
        {
            value = null;
            return false;
        }

        return Values.TryGetItem(key, out value);
    }

    public bool ContainsKey(string key) => _keys?.ContainsKey(key) ?? _values!.TryGetItem(key, out _);

    /// <summary>
    /// The hash of the value of <paramref name="key"/>, which must be defined.
    /// </summary>
    public Hash128 KeyHash(string key)
    {
        if (_keys != null)
        {
            return _keys[key];
        }

        return _keyHashes!.GetOrAdd(key, static (key, values) => NodeHash.Of(values.GetItem(key)), _values!);
    }
}

/// <summary>
/// Values that apply to the files of a directory and its subdirectories.
/// </summary>
internal sealed record FolderScope(FPath Directory, NodeObject Values)
{
    private Hash128? _hash;

    // 'with' expressions must not copy the cached hash.
    private FolderScope(FolderScope original)
    {
        Directory = original.Directory;
        Values = original.Values;
    }

    public Hash128 Hash => _hash ??= Hash128.Combine(Hash128.OfText(Directory.FullName), NodeHash.Of(Values));
}

internal enum ScopeKind { Own, Folder, Site, Fallback, None }

// Source: The directory of a folder scope, the source of a site scope, empty otherwise.
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

                builder[folder.Directory] = new FolderScope(folder.Directory, values);
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
    /// (<see cref="FromSettings"/>). A key the own metadata does not define is recorded in <paramref name="deps"/> with
    /// what it resolves to in the scopes (<see cref="ResolveHash"/>): a scope that does not define it could define it
    /// later and shadow the hit, a data file could change its value.
    /// </summary>
    public bool TryResolve(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, out Node? value, out ScopeHit hit)
    {
        return TryResolve(meta, key, fallback, deps, track: false, out value, out hit, out _);
    }

    /// <summary>
    /// Resolves a key like <see cref="TryResolve(Metadata, string, NodeObject?, DependencyTracker?, out Node?, out ScopeHit)"/>,
    /// for a template: instead of depending on the whole value, the unit depends on what the template reads of it,
    /// recorded by <paramref name="recorder"/> (null for a value that is not from the scopes, or when nothing is
    /// recorded).
    /// </summary>
    public bool TryResolveTracked(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, out Node? value, out AccessRecorder? recorder)
    {
        return TryResolve(meta, key, fallback, deps, track: true, out value, out _, out recorder);
    }

    private bool TryResolve(Metadata meta, string key, NodeObject? fallback, DependencyTracker? deps, bool track, out Node? value, out ScopeHit hit, out AccessRecorder? recorder)
    {
        recorder = null;

        if (meta.Own.TryGetItem(key, out value))
        {
            hit = new ScopeHit(ScopeKind.Own, "");
            return true;
        }

        AccessRecorder? tracked = null;

        if (deps != null)
        {
            if (track)
            {
                tracked = deps.TrackLookup(this, meta.Directory, key);
            }
            else
            {
                deps.RecordLookup(this, meta.Directory, key);
            }
        }

        if (TryResolveBeyondOwn(meta.Directory, key, out value, out hit))
        {
            recorder = tracked;
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

    /// <summary>
    /// The hash of what <paramref name="key"/> resolves to for a file of <paramref name="directory"/> whose own metadata
    /// does not define it, or <see cref="DependencySet.Absent"/> when no scope defines it. A unit that read the key can
    /// reuse its result while this hash does not change. When the settings define the key too, the hash combines both
    /// values: the settings may complete the value found.
    /// </summary>
    public Hash128 ResolveHash(FPath directory, string key)
    {
        Hash128 hash;

        if (TryResolveContributedHash(directory, key, out var found))
        {
            hash = _settings != null && _settings.ContainsKey(key) ? Hash128.Combine(found, _settings.KeyHash(key)) : found;
        }
        else
        {
            hash = _settings != null && _settings.ContainsKey(key) ? _settings.KeyHash(key) : DependencySet.Absent;
        }

        return hash;
    }

    /// <summary>
    /// The hash of what <paramref name="access"/> read of the value <paramref name="key"/> resolves to for a file of
    /// <paramref name="directory"/> whose own metadata does not define it (<see cref="ValueAccess.Project"/>), or
    /// <see cref="DependencySet.Absent"/> when no scope defines it.
    /// </summary>
    public Hash128 ProjectHash(FPath directory, string key, ValueAccess access)
    {
        return TryResolveBeyondOwn(directory, key, out var value, out _) ? Hash128.Combine(Present, access.Project(value)) : DependencySet.Absent;
    }

    private static readonly Hash128 Present = Hash128.OfText("present");

    /// <summary>
    /// Resolves a key in the folder scopes, the site scopes and the settings, completing an object with the settings'.
    /// </summary>
    private bool TryResolveBeyondOwn(FPath directory, string key, out Node? value, out ScopeHit hit)
    {
        if (TryResolveContributed(directory, key, out value, out hit))
        {
            if (_settings != null && value is NodeObject found && _settings.TryGetValue(key, out var setting) && setting is NodeObject defaults)
            {
                value = _completed!.GetValue(found, x => Complete(x, defaults));
            }

            return true;
        }

        if (_settings != null && _settings.TryGetValue(key, out value))
        {
            hit = new ScopeHit(ScopeKind.Site, _settings.Source);
            return true;
        }

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

    private bool TryResolveContributed(FPath directory, string key, out Node? value, out ScopeHit hit)
    {
        if (!_folders.IsEmpty)
        {
            for (var d = directory; !d.IsEmpty; d = d.GetParentDirectory())
            {
                if (_folders.TryGetValue(d, out var folder) && folder.Values.TryGetItem(key, out value))
                {
                    hit = new ScopeHit(ScopeKind.Folder, d.FullName);
                    return true;
                }
            }
        }

        foreach (var scope in _site)
        {
            if (scope.TryGetValue(key, out value))
            {
                hit = new ScopeHit(ScopeKind.Site, scope.Source);
                return true;
            }
        }

        value = null;
        hit = new ScopeHit(ScopeKind.None, "");
        return false;
    }

    private bool TryResolveContributedHash(FPath directory, string key, out Hash128 hash)
    {
        if (!_folders.IsEmpty)
        {
            for (var d = directory; !d.IsEmpty; d = d.GetParentDirectory())
            {
                if (_folders.TryGetValue(d, out var folder) && folder.Values.TryGetItem(key, out var value))
                {
                    hash = NodeHash.Of(value);
                    return true;
                }
            }
        }

        foreach (var scope in _site)
        {
            if (scope.ContainsKey(key))
            {
                hash = scope.KeyHash(key);
                return true;
            }
        }

        hash = default;
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
