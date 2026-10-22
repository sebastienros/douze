using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Cache;

/// <summary>
/// The disk cache of a site, in <c>&lt;cache dir&gt;/cache/</c>: the results of the units of previous builds, by result
/// key, with their outputs, contributions and dependencies, and the hashes of the source files. A build reads what
/// previous builds stored and stores what it runs; <see cref="Save"/> then writes the index of what the next build can
/// use.
/// </summary>
/// <remarks>
/// <para>Layout: <c>index.bin</c> (<see cref="CacheIndex"/>), <c>packs/*.pack</c> (blobs: contents, metadata and nodes
/// by hash, <see cref="PackFile"/>) and <c>lock</c>, held while a build uses the cache.</para>
/// <para>A successful build keeps the units it used, replayed or ran: the results of units that no longer exist (an
/// edited page's previous version, a deleted page) are dropped, so the cache does not grow with every edit. A build that
/// fails or is cancelled keeps the previous units too. Packs whose blobs are mostly unused are compacted.</para>
/// </remarks>
internal sealed class BuildCache : IDisposable, INodeStore, INodeSource
{
    private const string IndexFile = "index.bin";
    private const string PacksFolder = "packs";
    private const string LockFile = "lock";

    // A pack file whose time is that close to the build start may be changed again within the same time: its hash is
    // not trusted by the next build.
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly FileStream _lock;
    private readonly IProgressView _view;
    private readonly CacheIndex _previous;
    private readonly PackFile?[] _packs;
    private readonly Lock _writerLock = new();
    private readonly DateTime _started = DateTime.UtcNow;

    // Created on first use and read without the lock: volatile, so a reader that sees it sees it constructed.
    private volatile PackFile? _writer;

    private readonly ConcurrentDictionary<BlobId, BlobEntry> _written = new();
    private readonly ConcurrentDictionary<BlobId, byte> _writing = new();
    private readonly ConcurrentDictionary<Hash128, CachedUnit> _units = new();
    private readonly ConcurrentDictionary<Hash128, DependencySet> _dependencies = new();
    private readonly ConcurrentDictionary<FPath, SourceStat> _sources = new();
    private readonly ConcurrentDictionary<Hash128, Metadata> _metadata = new();
    private readonly ConcurrentDictionary<Hash128, Lazy<Node>> _nodes = new();

    private BuildCache(string directory, FileStream lockFile, IProgressView view, CacheIndex previous)
    {
        _directory = directory;
        _lock = lockFile;
        _view = view;
        _previous = previous;
        _packs = new PackFile?[previous.Packs.Count];
    }

    /// <summary>
    /// The folder of the cache: <c>&lt;cache dir&gt;/cache/</c>.
    /// </summary>
    public string Directory => _directory;

    /// <summary>
    /// The number of units the previous builds left.
    /// </summary>
    public int PreviousUnits => _previous.Units.Count;

    /// <summary>
    /// Opens the cache in <paramref name="directory"/>, or returns <c>null</c> when another build uses it. A cache written
    /// by another version of Douze is discarded; a damaged one is discarded with a warning.
    /// </summary>
    public static BuildCache? Open(string directory, IProgressView view)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(directory, PacksFolder));

        FileStream lockFile;

        try
        {
            lockFile = new FileStream(Path.Combine(directory, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            view.Message(LogLevel.Warning, null, $"the cache '{directory}' is used by another build: building without it");
            return null;
        }

        var index = LoadIndex(directory, view);
        var cache = new BuildCache(directory, lockFile, view, index);
        cache.DeleteUnusedPacks(index.Packs);
        return cache;
    }

    private static CacheIndex LoadIndex(string directory, IProgressView view)
    {
        var path = Path.Combine(directory, IndexFile);

        if (!File.Exists(path))
        {
            return new CacheIndex();
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);
            var index = CacheIndex.Read(stream, StepDefinition.DouzeVersion);

            if (index == null)
            {
                view.Message(LogLevel.Info, null, "the cache was written by another version of douze: starting a new one");
                return new CacheIndex();
            }

            foreach (var pack in index.Packs)
            {
                if (!File.Exists(Path.Combine(directory, PacksFolder, pack)))
                {
                    throw new InvalidDataException($"the pack '{pack}' is missing");
                }
            }

            return index;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            view.Message(LogLevel.Warning, null, $"the cache index '{path}' is damaged and was ignored: {e.Message}");
            return new CacheIndex();
        }
    }

    /// <summary>
    /// Deletes the packs the index does not list: left by a build that stopped before saving, or by a discarded index.
    /// </summary>
    private void DeleteUnusedPacks(IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.Ordinal);

        if (_writer != null)
        {
            kept.Add(Path.GetFileName(_writer.Path));
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Path.Combine(_directory, PacksFolder)))
        {
            if (!kept.Contains(Path.GetFileName(file)))
            {
                TryDelete(file);
            }
        }
    }

    public bool TryGetUnit(Hash128 key, [NotNullWhen(true)] out CachedUnit? unit) => _previous.Units.TryGetValue(key, out unit);

    public DependencySet GetDependencies(CachedUnit unit) => _previous.Dependencies[unit.Dependencies];

    /// <summary>
    /// A dependency set equal to <paramref name="set"/> that the cache already holds, so that units share one instance.
    /// </summary>
    public DependencySet Intern(DependencySet set)
    {
        if (set.IsEmpty)
        {
            return DependencySet.Empty;
        }

        if (_previous.Dependencies.TryGetValue(set.Hash, out var existing))
        {
            return existing;
        }

        return _dependencies.GetOrAdd(set.Hash, set);
    }

    /// <summary>
    /// The hash a source file had in a previous build, when its length and modification time did not change.
    /// </summary>
    public bool TryGetSourceHash(FPath path, long length, DateTime lastWriteTimeUtc, out Hash128 hash)
    {
        if (_previous.Sources.TryGetValue(path, out var stat) && stat.Length == length && stat.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            hash = stat.Hash;
            return true;
        }

        hash = default;
        return false;
    }

    /// <summary>
    /// Records the hash of a source file for the next build, unless its modification time is too recent to be trusted.
    /// </summary>
    public void PutSource(FPath path, long length, DateTime lastWriteTimeUtc, Hash128 hash)
    {
        if (lastWriteTimeUtc < _started - RacyWindow)
        {
            _sources[path] = new SourceStat(length, lastWriteTimeUtc, hash);
        }
    }

    /// <summary>
    /// Records that a unit of the previous builds was reused.
    /// </summary>
    public void Reused(Hash128 key, CachedUnit unit)
    {
        _units[key] = unit;
        _dependencies.TryAdd(unit.Dependencies, _previous.Dependencies[unit.Dependencies]);
    }

    /// <summary>
    /// Records the result of a unit that ran: its outputs and contribution must have been stored with
    /// <see cref="StoreOutputAsync"/> and <see cref="StoreScope"/>.
    /// </summary>
    public void Put(Hash128 key, CachedUnit unit, DependencySet dependencies)
    {
        _dependencies.TryAdd(dependencies.Hash, dependencies);
        _units[key] = unit with { Dependencies = dependencies.Hash };
    }

    /// <summary>
    /// Stores an output file of a unit: its content and metadata are stored unless they are those of an input.
    /// </summary>
    public async ValueTask<CachedOutput> StoreOutputAsync(VirtualFile file, IReadOnlyList<VirtualFile> inputs, CancellationToken ct)
    {
        var contentHash = file.ContentHash;
        var metadataHash = file.MetadataHash;

        if (!inputs.Any(x => x.ContentHash == contentHash))
        {
            var id = new BlobId(BlobKind.Content, contentHash);

            if (!Exists(id) && _writing.TryAdd(id, 0))
            {
                using var bytes = await file.Content.RentBytesAsync(ct);
                Append(id, bytes.Memory.Span, []);
            }
        }

        if (!inputs.Any(x => x.MetadataHash == metadataHash))
        {
            PutMetadata(file.Metadata);
        }

        var length = file.Content.Length ?? (await file.Content.ReadBytesAsync(ct)).Length;

        return new CachedOutput(file.Path, contentHash, length, file.Content.IsText, metadataHash, file.ContentType, file.SourcePath, file.LastWriteTimeUtc);
    }

    /// <summary>
    /// Stores the contribution of a reduce unit. The values of each scope are a node blob.
    /// </summary>
    public CachedScope StoreScope(ScopeSet contributions)
    {
        var site = ImmutableArray.CreateBuilder<CachedSiteScope>(contributions.Site.Count);

        foreach (var scope in contributions.Site)
        {
            var values = NodeHash.Of(scope.Values);
            PutNode(scope.Values, values);
            site.Add(new CachedSiteScope(scope.Source, values, [.. scope.Values.Select(x => (x.Key, scope.KeyHash(x.Key))).OrderBy(x => x.Key, StringComparer.Ordinal)]));
        }

        var folders = ImmutableArray.CreateBuilder<(FPath, Hash128)>(contributions.Folders.Count);

        foreach (var folder in contributions.Folders.Values.OrderBy(x => x.Directory))
        {
            var values = NodeHash.Of(folder.Values);
            PutNode(folder.Values, values);
            folders.Add((folder.Directory, values));
        }

        return new CachedScope(site.MoveToImmutable(), folders.MoveToImmutable());
    }

    /// <summary>
    /// Recreates an output file of a replayed unit. A file equal to an input is that input; otherwise its content and
    /// metadata are the input's when they are equal, or read from the cache when a step reads them.
    /// </summary>
    public VirtualFile Restore(CachedOutput output, IReadOnlyList<VirtualFile> inputs, Provenance provenance)
    {
        FileContent? content = null;
        Metadata? metadata = null;

        foreach (var input in inputs)
        {
            if (input.Path == output.Path && input.ContentHash == output.Content && input.MetadataHash == output.Metadata)
            {
                return input;
            }

            if (content == null && input.ContentHash == output.Content)
            {
                content = input.Content;
            }

            if (metadata == null && input.MetadataHash == output.Metadata)
            {
                metadata = input.Metadata;
            }
        }

        content ??= FileContent.FromLoader(output.Content, output.Length, output.IsText, ct => new(ReadBlob(new BlobId(BlobKind.Content, output.Content))));
        metadata ??= GetMetadata(output.Metadata);

        return new VirtualFile(output.Path, content, metadata, output.ContentType, provenance, output.LastWriteTimeUtc, output.SourcePath);
    }

    /// <summary>
    /// Recreates the contribution of a replayed reduce unit. The metadata its values refer to are the inputs' when they
    /// are equal; site values are read when a unit reads them.
    /// </summary>
    public ScopeSet RestoreScope(CachedScope scope, IReadOnlyList<VirtualFile> inputs)
    {
        foreach (var input in inputs)
        {
            _metadata.TryAdd(input.MetadataHash, input.Metadata);
        }

        var result = ScopeSet.Empty.WithFolders(scope.Folders.Select(x => new FolderScope(x.Directory, (NodeObject)GetNode(x.Values))));

        for (var i = scope.Site.Length - 1; i >= 0; i--)
        {
            var site = scope.Site[i];
            var values = site.Values;
            result = result.WithSite(new SiteScope(site.Source, values, site.Keys.ToDictionary(x => x.Key, x => x.Hash, StringComparer.Ordinal), () => (NodeObject)GetNode(values)));
        }

        return result;
    }

    public Metadata GetMetadata(Hash128 hash)
    {
        return _metadata.GetOrAdd(hash, static (hash, cache) => Metadata.FromLoader(hash, () =>
        {
            using var reader = new BinaryReader(new MemoryStream(cache.ReadBlob(new BlobId(BlobKind.Metadata, hash))), Encoding.UTF8);
            return NodeSerializer.ReadMetadata(reader, cache);
        }), this);
    }

    public Node GetNode(Hash128 hash)
    {
        return _nodes.GetOrAdd(hash, static (hash, cache) => new Lazy<Node>(() =>
        {
            var bytes = cache.ReadBlob(new BlobId(BlobKind.Node, hash));
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            var node = NodeSerializer.Read(reader, cache) ?? throw new InvalidDataException("A node blob is empty");

            // The hash is known: it is the blob's. The size is an estimate, only used to store the node apart again.
            switch (node)
            {
                case NodeObject obj: obj.HashCache.Set(hash, Math.Max(bytes.Length, NodeSerializer.SeparateSize + 1)); break;
                case NodeArray array: array.HashCache.Set(hash, Math.Max(bytes.Length, NodeSerializer.SeparateSize + 1)); break;
            }

            return node;
        }, LazyThreadSafetyMode.ExecutionAndPublication), this).Value;
    }

    public void PutNode(Node node, Hash128 hash)
    {
        var id = new BlobId(BlobKind.Node, hash);

        if (Exists(id) || !_writing.TryAdd(id, 0))
        {
            return;
        }

        var children = new ChildCollector(this);
        using var memory = new MemoryStream();

        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            NodeSerializer.Write(writer, node, children);
        }

        Append(id, memory.GetBuffer().AsSpan(0, (int)memory.Length), children.ToArray());
    }

    public void PutMetadata(Metadata metadata)
    {
        var id = new BlobId(BlobKind.Metadata, metadata.Hash);

        if (Exists(id) || !_writing.TryAdd(id, 0))
        {
            return;
        }

        var children = new ChildCollector(this);
        using var memory = new MemoryStream();

        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            NodeSerializer.WriteMetadata(writer, metadata, children);
        }

        Append(id, memory.GetBuffer().AsSpan(0, (int)memory.Length), children.ToArray());
    }

    private bool Exists(BlobId id) => _previous.Blobs.ContainsKey(id) || _written.ContainsKey(id);

    private void Append(BlobId id, ReadOnlySpan<byte> data, ImmutableArray<BlobId> children)
    {
        var writer = GetWriter();
        var offset = writer.Append(data);
        _written[id] = new BlobEntry(new BlobLocation(-1, offset, data.Length), children);
    }

    private PackFile GetWriter()
    {
        if (_writer != null)
        {
            return _writer;
        }

        lock (_writerLock)
        {
            return _writer ??= PackFile.Create(Path.Combine(_directory, PacksFolder, Guid.NewGuid().ToString("N") + ".pack"));
        }
    }

    private byte[] ReadBlob(BlobId id)
    {
        try
        {
            if (_previous.Blobs.TryGetValue(id, out var entry))
            {
                return GetPack(entry.Location.Pack).Read(entry.Location.Offset, entry.Location.Length);
            }

            if (_written.TryGetValue(id, out entry))
            {
                return _writer!.Read(entry.Location.Offset, entry.Location.Length);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new DouzeException($"cannot read the cache '{_directory}' ({e.Message}): build with --no-cache, or delete it");
        }

        throw new DouzeException($"the cache '{_directory}' has no {id.Kind.ToString().ToLowerInvariant()} {id.Hash}: build with --no-cache, or delete it");
    }

    private PackFile GetPack(int index)
    {
        if (Volatile.Read(ref _packs[index]) is { } pack)
        {
            return pack;
        }

        lock (_packs)
        {
            if (_packs[index] is { } opened)
            {
                return opened;
            }

            var created = PackFile.OpenRead(Path.Combine(_directory, PacksFolder, _previous.Packs[index]));
            Volatile.Write(ref _packs[index], created);
            return created;
        }
    }

    /// <summary>
    /// Writes the index of what the next build can use. <paramref name="succeeded"/>: whether the build succeeded, in
    /// which case the units it did not use are dropped.
    /// </summary>
    public void Save(bool succeeded)
    {
        try
        {
            SaveCore(succeeded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DouzeException)
        {
            _view.Message(LogLevel.Warning, null, $"cannot save the cache '{_directory}': {e.Message}");
        }
    }

    private void SaveCore(bool succeeded)
    {
        _writer?.Flush();

        // Nothing ran and every unit was reused: the index on disk is the one to keep.
        if (_written.IsEmpty && _units.Count == _previous.Units.Count && _sources.Count == _previous.Sources.Count
            && _units.All(x => _previous.Units.ContainsKey(x.Key)) && _sources.All(x => _previous.Sources.TryGetValue(x.Key, out var s) && s == x.Value))
        {
            DeleteWriter();
            return;
        }

        var index = new CacheIndex();
        var units = new Dictionary<Hash128, CachedUnit>(_units);

        if (!succeeded)
        {
            foreach (var (key, unit) in _previous.Units)
            {
                units.TryAdd(key, unit);
            }
        }

        // The blobs the kept units use, and their children.
        var live = new HashSet<BlobId>();
        var pending = new Stack<BlobId>();

        void Use(BlobId id)
        {
            if (live.Add(id))
            {
                pending.Push(id);
            }
        }

        foreach (var (key, unit) in units)
        {
            index.Units[key] = unit;
            index.Dependencies[unit.Dependencies] = _dependencies.TryGetValue(unit.Dependencies, out var set) ? set : _previous.Dependencies[unit.Dependencies];

            foreach (var output in unit.Outputs)
            {
                Use(new BlobId(BlobKind.Content, output.Content));
                Use(new BlobId(BlobKind.Metadata, output.Metadata));
            }

            if (unit.Scope is { } scope)
            {
                foreach (var site in scope.Site)
                {
                    Use(new BlobId(BlobKind.Node, site.Values));
                }

                foreach (var (_, values) in scope.Folders)
                {
                    Use(new BlobId(BlobKind.Node, values));
                }
            }
        }

        var entries = new Dictionary<BlobId, BlobEntry>();

        while (pending.TryPop(out var id))
        {
            // Outputs whose content or metadata are an input's have no blob.
            if (!_written.TryGetValue(id, out var entry) && !_previous.Blobs.TryGetValue(id, out entry))
            {
                continue;
            }

            entries[id] = entry;

            foreach (var child in entry.Children)
            {
                Use(child);
            }
        }

        // Packs: the previous ones, then this build's (-1). Mostly unused packs are compacted into a new one.
        var packLengths = new Dictionary<int, long>();
        var liveLengths = new Dictionary<int, long>();

        for (var i = 0; i < _previous.Packs.Count; i++)
        {
            packLengths[i] = new FileInfo(Path.Combine(_directory, PacksFolder, _previous.Packs[i])).Length;
        }

        if (_writer != null)
        {
            packLengths[-1] = _writer.Length;
        }

        foreach (var entry in entries.Values)
        {
            liveLengths[entry.Location.Pack] = liveLengths.GetValueOrDefault(entry.Location.Pack) + entry.Location.Length;
        }

        var total = packLengths.Values.Sum();
        var used = liveLengths.Values.Sum();
        var compact = total > 4 << 20 && used < total / 2;
        var packNumbers = new Dictionary<int, int>();
        PackFile? compacted = null;

        if (compact)
        {
            compacted = PackFile.Create(Path.Combine(_directory, PacksFolder, Guid.NewGuid().ToString("N") + ".pack"));
            index.Packs.Add(Path.GetFileName(compacted.Path));

            foreach (var (id, entry) in entries.OrderBy(x => x.Value.Location.Pack).ThenBy(x => x.Value.Location.Offset))
            {
                var bytes = entry.Location.Pack < 0 ? _writer!.Read(entry.Location.Offset, entry.Location.Length) : GetPack(entry.Location.Pack).Read(entry.Location.Offset, entry.Location.Length);
                index.Blobs[id] = entry with { Location = new BlobLocation(0, compacted.Append(bytes), bytes.Length) };
            }

            compacted.Flush();
            compacted.Dispose();
        }
        else
        {
            foreach (var pack in liveLengths.Keys.Order())
            {
                packNumbers[pack] = index.Packs.Count;
                index.Packs.Add(pack < 0 ? Path.GetFileName(_writer!.Path) : _previous.Packs[pack]);
            }

            foreach (var (id, entry) in entries)
            {
                index.Blobs[id] = entry with { Location = entry.Location with { Pack = packNumbers[entry.Location.Pack] } };
            }
        }

        foreach (var (path, stat) in _sources)
        {
            index.Sources[path] = stat;
        }

        if (!succeeded)
        {
            foreach (var (path, stat) in _previous.Sources)
            {
                index.Sources.TryAdd(path, stat);
            }
        }

        var temporary = Path.Combine(_directory, IndexFile + ".tmp");

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16))
        {
            index.Write(stream, StepDefinition.DouzeVersion);
        }

        File.Move(temporary, Path.Combine(_directory, IndexFile), overwrite: true);

        if (_writer != null && !index.Packs.Contains(Path.GetFileName(_writer.Path)))
        {
            DeleteWriter();
        }

        ClosePacks();
        DeleteUnusedPacks(index.Packs);
    }

    private void DeleteWriter()
    {
        if (_writer is { } writer)
        {
            writer.Dispose();
            TryDelete(writer.Path);
            _writer = null;
        }
    }

    private void ClosePacks()
    {
        for (var i = 0; i < _packs.Length; i++)
        {
            _packs[i]?.Dispose();
            _packs[i] = null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use (on Windows): deleted by a later build.
        }
    }

    public void Dispose()
    {
        ClosePacks();
        _writer?.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Stores what a blob refers to through the cache, and collects it as the blob's children.
    /// </summary>
    private sealed class ChildCollector(BuildCache cache) : INodeStore
    {
        private readonly List<BlobId> _children = [];

        public void PutNode(Node node, Hash128 hash)
        {
            cache.PutNode(node, hash);
            _children.Add(new BlobId(BlobKind.Node, hash));
        }

        public void PutMetadata(Metadata metadata)
        {
            cache.PutMetadata(metadata);
            _children.Add(new BlobId(BlobKind.Metadata, metadata.Hash));
        }

        public ImmutableArray<BlobId> ToArray() => [.. _children.Distinct()];
    }
}
