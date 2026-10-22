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

    // A source file whose time is that close to the build start may be changed again within the same time: its hash is
    // not trusted by the next build.
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly FileStream _lock;
    private readonly IProgressView _view;
    private readonly Lock _writerLock = new();

    // The results of the previous builds, read from the index or committed by the previous build of a watch session,
    // the packs that hold their blobs, and whether the index on disk is that one.
    private CacheIndex _previous;
    private PackFile?[] _packs;
    private bool _persisted = true;

    // The start of the build in progress: source files changed less than RacyWindow before it are read again.
    private readonly TimeProvider _time;
    private DateTime _started;

    // Created on first use and read without the lock: volatile, so a reader that sees it sees it constructed.
    private volatile PackFile? _writer;

    private readonly ConcurrentDictionary<BlobId, BlobEntry> _written = new();
    private readonly ConcurrentDictionary<BlobId, byte> _writing = new();
    private ConcurrentDictionary<Hash128, byte> _reused;
    private readonly ConcurrentDictionary<Hash128, CachedUnit> _put = new();
    private readonly ConcurrentDictionary<Hash128, DependencySet> _dependencies = new();
    private ConcurrentDictionary<FPath, SourceStat> _sources;
    private readonly ConcurrentDictionary<Hash128, Metadata> _metadata = new();
    private readonly ConcurrentDictionary<Hash128, Lazy<Node>> _nodes = new();
    private readonly ConcurrentDictionary<Hash128, (string Owner, Hash128 Value)> _memos = new();
    private readonly ConcurrentDictionary<string, byte> _memoOwners = new(StringComparer.Ordinal);

    private BuildCache(string directory, FileStream lockFile, IProgressView view, CacheIndex previous, TimeProvider time)
    {
        _time = time;
        _started = time.GetUtcNow().UtcDateTime;
        _directory = directory;
        _lock = lockFile;
        _view = view;
        _previous = previous;
        _packs = new PackFile?[previous.Packs.Count];
        (_reused, _sources) = NewBuildSets(previous);
    }

    /// <summary>
    /// The sets of a build that hold an entry per unit or source file, sized for the previous build: growing a
    /// concurrent dictionary locks it whole, and a rebuild reuses about every unit.
    /// </summary>
    private static (ConcurrentDictionary<Hash128, byte>, ConcurrentDictionary<FPath, SourceStat>) NewBuildSets(CacheIndex previous)
    {
        return (new(Environment.ProcessorCount, previous.Units.Count), new(Environment.ProcessorCount, previous.Sources.Count));
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
    /// The number of packs the previous results use.
    /// </summary>
    public int PackCount => _previous.Packs.Count;

    /// <summary>
    /// Opens the cache in <paramref name="directory"/>, or returns <c>null</c> when another build uses it. A cache written
    /// by another version of Douze is discarded; a damaged one is discarded with a warning.
    /// </summary>
    public static BuildCache? Open(string directory, IProgressView view, TimeProvider? time = null)
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
        var cache = new BuildCache(directory, lockFile, view, index, time ?? TimeProvider.System);
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

            // A pack shorter than the blobs the index places in it was truncated (a build that stopped while the
            // system was writing it): the index is not usable.
            var lengths = new long[index.Packs.Count];

            for (var i = 0; i < lengths.Length; i++)
            {
                var pack = new FileInfo(Path.Combine(directory, PacksFolder, index.Packs[i]));

                if (!pack.Exists)
                {
                    throw new InvalidDataException($"the pack '{index.Packs[i]}' is missing");
                }

                lengths[i] = pack.Length;
            }

            foreach (var entry in index.Blobs.Values)
            {
                if (entry.Location.Offset + entry.Location.Length > lengths[entry.Location.Pack])
                {
                    throw new InvalidDataException($"the pack '{index.Packs[entry.Location.Pack]}' is shorter than its blobs");
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
    /// The value that step <paramref name="owner"/> stored with <see cref="PutMemo"/> under <paramref name="key"/> in
    /// this build or a previous one, such as the result of a code snippet that a unit of many snippets computed: the
    /// next run of the unit reuses the snippets that did not change. Once a step reads or stores a memo in a build, the
    /// build keeps only the memos of the step that it read or stored (as it keeps the units it used); the memos of a
    /// step that did not run (its units were replayed) are all kept.
    /// </summary>
    public bool TryGetMemo(string owner, Hash128 key, [NotNullWhen(true)] out byte[]? value)
    {
        _memoOwners.TryAdd(owner, 0);

        if (_memos.TryGetValue(key, out var memo) || _previous.Memos.TryGetValue(key, out memo))
        {
            value = ReadBlob(new BlobId(BlobKind.Content, memo.Value));
            _memos.TryAdd(key, (owner, memo.Value));
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Stores a memo of step <paramref name="owner"/> (<see cref="TryGetMemo"/>).
    /// </summary>
    public void PutMemo(string owner, Hash128 key, ReadOnlySpan<byte> value)
    {
        _memoOwners.TryAdd(owner, 0);
        var hash = Hash128.Of(value);
        var id = new BlobId(BlobKind.Content, hash);

        if (!Exists(id) && _writing.TryAdd(id, 0))
        {
            Append(id, value, []);
        }

        _memos[key] = (owner, hash);
    }

    /// <summary>
    /// Records that a unit of the previous builds was reused.
    /// </summary>
    public void Reused(Hash128 key, CachedUnit unit)
    {
        _reused.TryAdd(key, 0);
    }

    /// <summary>
    /// Records the result of a unit that ran: its outputs and contribution must have been stored with
    /// <see cref="StoreOutputAsync"/> and <see cref="StoreScope"/>.
    /// </summary>
    public void Put(Hash128 key, CachedUnit unit, DependencySet dependencies)
    {
        if (!_previous.Dependencies.ContainsKey(dependencies.Hash))
        {
            _dependencies.TryAdd(dependencies.Hash, dependencies);
        }

        _put[key] = unit with { Dependencies = dependencies.Hash };
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
    /// Writes the index of what the next build can use (<see cref="Commit"/>, then <see cref="Persist"/>).
    /// <paramref name="succeeded"/>: whether the build succeeded, in which case the units it did not use are dropped.
    /// </summary>
    public void Save(bool succeeded)
    {
        try
        {
            CommitCore(succeeded);
            PersistCore();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DouzeException)
        {
            _view.Message(LogLevel.Warning, null, $"cannot save the cache '{_directory}': {e.Message}");
        }
    }

    /// <summary>
    /// Makes what the build used and stored the previous results of the next build, in memory: a watch session keeps
    /// the cache open across its rebuilds and writes the index once, with <see cref="Persist"/>.
    /// <paramref name="succeeded"/>: whether the build succeeded, in which case the units it did not use are dropped.
    /// </summary>
    public void Commit(bool succeeded)
    {
        try
        {
            CommitCore(succeeded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DouzeException)
        {
            _view.Message(LogLevel.Warning, null, $"cannot save the cache '{_directory}': {e.Message}");
        }
    }

    /// <summary>
    /// Writes the index of the committed results when it changed since it was read or last written, compacting the
    /// packs when most of their bytes are unused, and deletes the packs it does not list.
    /// </summary>
    public void Persist()
    {
        try
        {
            PersistCore();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DouzeException)
        {
            _view.Message(LogLevel.Warning, null, $"cannot save the cache '{_directory}': {e.Message}");
        }
    }

    /// <summary>
    /// Starts a build: the modification times of the source files are trusted from this time on (watch mode starts a
    /// build for every change; a build that opens the cache starts when it opens it).
    /// </summary>
    public void BeginBuild()
    {
        _started = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// The files that the units of the previous builds wrote to the output folder, with their length and modification
    /// time once written (<see cref="DependencySet.Written"/>). A path can be listed twice when a failed build kept the
    /// units of an older one.
    /// </summary>
    public IEnumerable<(FPath Path, long Length, DateTime LastWriteTimeUtc)> PreviousWritten()
    {
        foreach (var unit in _previous.Units.Values)
        {
            foreach (var written in _previous.Dependencies[unit.Dependencies].Written)
            {
                yield return written;
            }
        }
    }

    /// <summary>
    /// Updates the previous results in place: the units this build used (and, after a failure, the previous ones too),
    /// the blobs it wrote, in its pack, and the stats of the source files. Nothing is collected: blobs, dependency sets
    /// and packs that nothing uses anymore stay until <see cref="PersistCore"/>, so that a rebuild of a watch session
    /// costs what it changed, not the size of the cache.
    /// </summary>
    private void CommitCore(bool succeeded)
    {
        var index = _previous;
        var changed = false;

        // Every previous unit was reused when as many were: nothing to drop.
        if (succeeded && _reused.Count != index.Units.Count)
        {
            foreach (var key in index.Units.Keys)
            {
                if (!_reused.ContainsKey(key) && !_put.ContainsKey(key))
                {
                    index.Units.Remove(key);
                    changed = true;
                }
            }
        }

        foreach (var (key, unit) in _put)
        {
            index.Units[key] = unit;
            changed = true;
        }

        foreach (var (hash, set) in _dependencies)
        {
            index.Dependencies.TryAdd(hash, set);
        }

        // The memos this build read or stored; those it did not, of the steps that read or stored some, are dropped
        // when it succeeded.
        if (succeeded && !_memoOwners.IsEmpty)
        {
            foreach (var (key, (owner, _)) in index.Memos)
            {
                if (_memoOwners.ContainsKey(owner) && !_memos.ContainsKey(key))
                {
                    index.Memos.Remove(key);
                    changed = true;
                }
            }
        }

        foreach (var (key, value) in _memos)
        {
            if (!index.Memos.TryGetValue(key, out var previous) || previous != value)
            {
                index.Memos[key] = value;
                changed = true;
            }
        }

        // The packs of this build are read like the others from now on.
        if (_writer is { } writer)
        {
            writer.Flush();
            writer.Dispose();
            _writer = null;

            if (_written.IsEmpty)
            {
                TryDelete(writer.Path);
            }
            else
            {
                var pack = index.Packs.Count;
                index.Packs.Add(Path.GetFileName(writer.Path));

                foreach (var (id, entry) in _written)
                {
                    index.Blobs[id] = entry with { Location = entry.Location with { Pack = pack } };
                }

                Array.Resize(ref _packs, index.Packs.Count);
                changed = true;
            }
        }

        // The stats of the files this build saw; after a failure, those of the previous builds too.
        if (succeeded && (_sources.Count != index.Sources.Count || _sources.Any(x => !index.Sources.TryGetValue(x.Key, out var s) || s != x.Value)))
        {
            index.Sources.Clear();

            foreach (var (path, stat) in _sources)
            {
                index.Sources[path] = stat;
            }

            changed = true;
        }
        else if (!succeeded)
        {
            foreach (var (path, stat) in _sources)
            {
                if (!index.Sources.TryGetValue(path, out var previous) || previous != stat)
                {
                    index.Sources[path] = stat;
                    changed = true;
                }
            }
        }

        _written.Clear();
        _writing.Clear();
        _put.Clear();
        _dependencies.Clear();
        _memos.Clear();
        _memoOwners.Clear();
        (_reused, _sources) = NewBuildSets(index);

        if (changed)
        {
            _persisted = false;
        }
    }

    /// <summary>
    /// Writes the previous results to the index: only the blobs and dependency sets their units use, in the packs that
    /// hold them, compacted into a new pack when most of their bytes are unused. Then deletes the other packs.
    /// </summary>
    private void PersistCore()
    {
        if (_persisted)
        {
            return;
        }

        var previous = _previous;
        var index = new CacheIndex();

        // The blobs the units use, and their children.
        var live = new HashSet<BlobId>();
        var pending = new Stack<BlobId>();

        void Use(BlobId id)
        {
            if (live.Add(id))
            {
                pending.Push(id);
            }
        }

        foreach (var (key, unit) in previous.Units)
        {
            index.Units[key] = unit;
            index.Dependencies[unit.Dependencies] = previous.Dependencies[unit.Dependencies];

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

        foreach (var (key, memo) in previous.Memos)
        {
            index.Memos[key] = memo;
            Use(new BlobId(BlobKind.Content, memo.Value));
        }

        var entries = new Dictionary<BlobId, BlobEntry>();

        while (pending.TryPop(out var id))
        {
            // Outputs whose content or metadata are an input's have no blob.
            if (!previous.Blobs.TryGetValue(id, out var entry))
            {
                continue;
            }

            entries[id] = entry;

            foreach (var child in entry.Children)
            {
                Use(child);
            }
        }

        foreach (var (path, stat) in previous.Sources)
        {
            index.Sources[path] = stat;
        }

        // Packs: the ones that still hold a used blob; mostly unused packs are compacted into a new one.
        var used = entries.Values.Sum(x => (long)x.Location.Length);
        var packs = entries.Values.Select(x => x.Location.Pack).Distinct().Order().ToArray();
        var total = packs.Sum(x => new FileInfo(Path.Combine(_directory, PacksFolder, previous.Packs[x])).Length);

        if (total > 4 << 20 && used < total / 2)
        {
            using var compacted = PackFile.Create(Path.Combine(_directory, PacksFolder, Guid.NewGuid().ToString("N") + ".pack"));
            index.Packs.Add(Path.GetFileName(compacted.Path));

            foreach (var (id, entry) in entries.OrderBy(x => x.Value.Location.Pack).ThenBy(x => x.Value.Location.Offset))
            {
                var bytes = GetPack(entry.Location.Pack).Read(entry.Location.Offset, entry.Location.Length);
                index.Blobs[id] = entry with { Location = new BlobLocation(0, compacted.Append(bytes), bytes.Length) };
            }

            compacted.Flush();
        }
        else
        {
            var packNumbers = new Dictionary<int, int>();

            foreach (var pack in packs)
            {
                packNumbers[pack] = index.Packs.Count;
                index.Packs.Add(previous.Packs[pack]);
            }

            foreach (var (id, entry) in entries)
            {
                index.Blobs[id] = entry with { Location = entry.Location with { Pack = packNumbers[entry.Location.Pack] } };
            }
        }

        var temporary = Path.Combine(_directory, IndexFile + ".tmp");

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16))
        {
            index.Write(stream, StepDefinition.DouzeVersion);
        }

        File.Move(temporary, Path.Combine(_directory, IndexFile), overwrite: true);

        Roll(index);
        _persisted = true;
        DeleteUnusedPacks(index.Packs);
    }

    /// <summary>
    /// Makes <paramref name="index"/> the previous results. The metadata and nodes read from blobs that the index keeps
    /// stay: the next build of a watch session reads them again (collections read the metadata of every page).
    /// </summary>
    private void Roll(CacheIndex index)
    {
        ClosePacks();

        _previous = index;
        _packs = new PackFile?[index.Packs.Count];

        foreach (var hash in _metadata.Keys)
        {
            if (!index.Blobs.ContainsKey(new BlobId(BlobKind.Metadata, hash)))
            {
                _metadata.TryRemove(hash, out _);
            }
        }

        foreach (var hash in _nodes.Keys)
        {
            if (!index.Blobs.ContainsKey(new BlobId(BlobKind.Node, hash)))
            {
                _nodes.TryRemove(hash, out _);
            }
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
