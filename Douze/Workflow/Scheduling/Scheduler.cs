using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Douze.FileSystems;
using Douze.Tracing;
using Douze.Workflow.Cache;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// What a unit that ran or replayed gives: its outputs, what it read of its file's own metadata (null when it was
/// replayed or not recorded) and its dependencies.
/// </summary>
internal sealed record UnitOutcome(FileList Outputs, OwnReads? Reads, DependencySet Dependencies);

internal sealed record SchedulerResult(IReadOnlyList<StepRun> Runs, IReadOnlyList<BuildError> Errors, ScopeSet FinalScopes, bool Cancelled, SourceSnapshot? Sources);

/// <summary>
/// Runs a workflow graph: streams source files into root steps, runs each step according to its shape, merges scope
/// contributions, and applies the failure policy.
/// </summary>
internal sealed class Scheduler
{
    private const string UnitCategory = "unit";

    // Files a bounded channel holds per unit that can run at once (BuildOptions.MaxDegreeOfParallelism).
    private const int FilesPerSlot = 2;

    private readonly WorkflowGraph _graph;
    private readonly BuildContext _context;
    private readonly ReplayState? _replay;

    // Equal dependency sets are shared by the units that recorded them (the cache shares them otherwise).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Hash128, DependencySet> _dependencySets = new();

    public Scheduler(WorkflowGraph graph, BuildContext context)
    {
        _graph = graph;
        _context = context;
        _replay = context.Cache is { } cache ? new ReplayState(cache, context) : null;
    }

    public async Task<SchedulerResult> RunAsync(CancellationToken ct)
    {
        using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var runners = new NodeRunner[_graph.Nodes.Count];

        foreach (var node in _graph.Nodes)
        {
            runners[node.Index] = new NodeRunner(this, node, runners, buildCts);
        }

        var sources = await EnumerateSourcesAsync();

        await Task.WhenAll(runners.Select(x => x.RunAsync(sources)));

        var runs = runners.Select(x => x.Run).ToArray();
        var errors = runs.SelectMany(x => x.Errors).ToArray();

        // Nearest contributions first: descending topological index.
        var finalScopes = ScopeSet.Merge(runs.Reverse().Select(x => x.Contributions).ToArray(), _context.BaseScopes);

        return new SchedulerResult(runs, errors, finalScopes, ct.IsCancellationRequested, Sources);
    }

    /// <summary>
    /// The source files of the roots, sorted by path, from the snapshot of the source folder that the build was given
    /// (watch mode) or from a scan of the folders the <c>inputs</c> of the roots can match files in
    /// (<see cref="SourceScope"/>). Files under the output, cache and <c>.douze</c> folders are excluded when they are
    /// inside the source folder. A file whose length and modification time did not change since a build that cached its hash
    /// is not read to be hashed.
    /// </summary>
    private async Task<SourceFiles> EnumerateSourcesAsync()
    {
        var roots = _graph.Nodes.Where(x => x.Inputs != null).ToArray();

        if (roots.Length == 0)
        {
            return new SourceFiles(roots, []);
        }

        var source = _context.Source;
        var scope = SourceScope.Of(roots.Select(x => x.Inputs!));
        var excluded = _context.ExcludedSourceFolders;

        // A watch session's snapshot, while it lists every folder the roots read.
        var snapshot = _context.Options.Sources is { } kept && kept.Covers(scope, excluded)
            ? kept
            : await SourceSnapshot.ScanAsync(source, scope, excluded, _context.MaxDegreeOfParallelism, _context.Cancellation);

        Sources = snapshot;

        var entries = snapshot.Matching(_graph, roots, _context.ExcludedSourceFolders);
        var files = new VirtualFile[entries.Count];
        var cache = _context.Cache;

        for (var i = 0; i < files.Length; i++)
        {
            var (path, length, date, _) = entries[i];
            FileContent content;

            if (cache == null)
            {
                content = FileContent.FromFile(source, path);
            }
            else if (cache.TryGetSourceHash(path, length, date, out var hash))
            {
                cache.PutSource(path, length, date, hash);
                content = FileContent.FromFile(source, path, hash, null);
            }
            else
            {
                content = FileContent.FromFile(source, path, null, hash => cache.PutSource(path, length, date, hash));
            }

            files[i] = VirtualFile.FromSource(path, content, date);
        }

        var byRoot = new List<VirtualFile>[roots.Length];

        for (var r = 0; r < byRoot.Length; r++)
        {
            byRoot[r] = [];
        }

        for (var i = 0; i < files.Length; i++)
        {
            foreach (var r in entries[i].Roots)
            {
                byRoot[r].Add(files[i]);
            }
        }

        return new SourceFiles(roots, byRoot);
    }

    /// <summary>
    /// The snapshot of the source folder the build used, once the sources are enumerated.
    /// </summary>
    public SourceSnapshot? Sources { get; private set; }

    /// <summary>
    /// The capacity of the channel from <paramref name="producer"/> to <paramref name="consumer"/>, null for unbounded.
    /// A map step reads as soon as its needs write, so a few files per slot are enough and the producer waits beyond
    /// them: the files do not pile up in front of the slowest step. A barrier (reduce, group) holds its whole input.
    /// </summary>
    private int? GetCapacity(GraphNode producer, GraphNode consumer)
    {
        if (consumer.Step.EffectiveShape != StepShape.Map)
        {
            return null;
        }

        // A step that reads no scopes reads as soon as its needs write, whatever runs upstream of it.
        if (!consumer.Step.ReadsScopes)
        {
            return FilesPerSlot * _context.MaxDegreeOfParallelism;
        }

        // A step reads nothing before the reduce steps upstream of it published their contributions. When a full
        // channel would stop a node that feeds one of them (the producer, or one of its ancestors, which stop once the
        // producer stops reading), the reduce step would wait for files waiting for it: the channel holds everything.
        // Reduce steps upstream of the producer published theirs before the producer started.
        var producerAncestors = _graph.Ancestors(producer.Definition.Name);

        foreach (var name in _graph.Ancestors(consumer.Definition.Name))
        {
            if (_graph[name].Step.EffectiveShape != StepShape.Reduce || producerAncestors.Contains(name))
            {
                continue;
            }

            var reduceAncestors = _graph.Ancestors(name);

            if (name == producer.Definition.Name || reduceAncestors.Contains(producer.Definition.Name) || producerAncestors.Overlaps(reduceAncestors))
            {
                return null;
            }
        }

        return FilesPerSlot * _context.MaxDegreeOfParallelism;
    }

    /// <summary>
    /// The source files of each root, released once every root has read them, so that their contents do not stay in
    /// memory until the build ends.
    /// </summary>
    private sealed class SourceFiles(GraphNode[] roots, List<VirtualFile>[] files)
    {
        private List<VirtualFile>[]? _files = files;
        private int _readers = roots.Length;

        public IReadOnlyList<VirtualFile> FilesOf(GraphNode root)
        {
            return _files is { } files ? files[Array.IndexOf(roots, root)] : [];
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _readers) == 0)
            {
                _files = null;
            }
        }
    }

    /// <summary>
    /// Runs one node: waits for the contributions of its ancestors, reads its entering files, runs its step, and
    /// completes its channel with the outcome.
    /// </summary>
    private sealed class NodeRunner
    {
        private readonly Scheduler _scheduler;
        private readonly NodeRunner[] _runners;
        private readonly CancellationTokenSource _buildCts;
        private readonly CancellationTokenSource _stepCts;
        private readonly Lock _pathsLock = new();
        private readonly Dictionary<FPath, (string Producer, FileRef Ref)>? _producers;
        private readonly Lock _startLock = new();
        private int _started;
        private TraceSpan _span;
        private bool _upstreamUnavailable;
        private int _sourcesReleased;
        private int _draining;

        // Set once the step is initialized: the cache, when the step's results are cached, and what its result keys
        // depend on beyond its definition and inputs.
        private ReplayState? _replay;
        private Hash128 _version;

        public NodeRunner(Scheduler scheduler, GraphNode node, NodeRunner[] runners, CancellationTokenSource buildCts)
        {
            _scheduler = scheduler;
            _runners = runners;
            _buildCts = buildCts;
            _stepCts = CancellationTokenSource.CreateLinkedTokenSource(buildCts.Token);
            Node = node;
            Run = new StepRun(node);
            Output = new FileChannel(node.Dependents.Select(x => (x.Definition.Name, scheduler.GetCapacity(node, x))));

            // A path can only come from two producers when there are several.
            if (node.Needs.Count + (node.Inputs != null ? 1 : 0) > 1)
            {
                _producers = [];
            }

            // Only reduce steps contribute scopes; the others never make their descendants wait.
            if (node.Step.EffectiveShape != StepShape.Reduce)
            {
                ContributionReady.SetResult();
            }
        }

        public GraphNode Node { get; }

        public StepRun Run { get; }

        public FileChannel Output { get; }

        /// <summary>
        /// Completed once <see cref="StepRun.Contributions"/> is final, before the channel completes.
        /// </summary>
        public TaskCompletionSource ContributionReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completed once the node's state is final and its channel is complete.
        /// </summary>
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private BuildContext Context => _scheduler._context;

        private string Name => Node.Definition.Name;

        /// <summary>
        /// Runs the node and always publishes its outcome: whatever happens, its contribution, its channel and
        /// <see cref="Completed"/> complete, since its dependents wait for them.
        /// </summary>
        public async Task RunAsync(SourceFiles sources)
        {
            try
            {
                await RunStepAsync(sources);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                ReleaseSources(sources);
                DrainInputs();
                ContributionReady.TrySetResult();
                Output.Complete(Run.State == StepState.Succeeded ? null : new StepUnavailableException(Name, Run.State));
                Completed.TrySetResult();
            }
        }

        private void ReleaseSources(SourceFiles sources)
        {
            if (Node.Inputs != null && Interlocked.Exchange(ref _sourcesReleased, 1) == 0)
            {
                sources.Release();
            }
        }

        /// <summary>
        /// Reads what remains in the channels of the needs and drops it, once the node stopped reading them. A node that
        /// stopped early (a failure, a skip, a cancellation) must not leave its needs waiting on a full channel; a node
        /// that read its inputs to the end finds them complete. Called once the node's own readers have returned, since
        /// each channel has a single reader.
        /// </summary>
        private void DrainInputs()
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0)
            {
                return;
            }

            foreach (var need in Node.Needs)
            {
                _ = DrainAsync(_runners[need.Index].Output.Subscribe(Name));
            }

            static async Task DrainAsync(ChannelReader<VirtualFile> reader)
            {
                try
                {
                    while (await reader.WaitToReadAsync(CancellationToken.None))
                    {
                        while (reader.TryRead(out _))
                        {
                        }
                    }
                }
                catch (Exception)
                {
                    // The need did not succeed: its own run reports why.
                }
            }
        }

        /// <summary>
        /// An exception outside the units and the step's own code, which report their failures: in the scheduler, the
        /// view or the reporting of a failure, for instance for lack of memory. The step fails and the build stops,
        /// since the state of the build is not known.
        /// </summary>
        private void Fail(Exception e)
        {
            try
            {
                Run.AddError(BuildError.FromException(Name, null, e));
            }
            finally
            {
                Run.EndedAt ??= DateTimeOffset.UtcNow;
                Run.StartedAt ??= Run.EndedAt;
                Run.State = StepState.Failed;
                Run.Seal();
                _span.Dispose();
                _buildCts.Cancel();
            }
        }

        private async Task RunStepAsync(SourceFiles sources)
        {
            var token = _stepCts.Token;
            var initialized = false;

            try
            {
                // A step sees the contributions of every ancestor, nearest first (descending topological index). A step
                // that reads no scopes sees only the base scopes, and does not wait.
                var ancestors = Node.Step.ReadsScopes
                    ? _scheduler._graph.Ancestors(Name).Select(x => _runners[_scheduler._graph[x].Index]).OrderByDescending(x => x.Node.Index).ToArray()
                    : [];
                await Task.WhenAll(ancestors.Select(x => x.ContributionReady.Task)).WaitAsync(token);
                Run.Scopes = ScopeSet.Merge(ancestors.Select(x => x.Run.Contributions).ToArray(), Context.BaseScopes);

                Run.State = StepState.Running;

                initialized = true;
                await Node.Step.InitializeAsync(Context, token);

                if (Node.Definition.Incremental && _scheduler._replay is { } replay)
                {
                    _replay = replay;
                    _version = Node.Step.TracksDependencies ? Node.Step.CacheVersion : Hash128.Combine(Node.Step.CacheVersion, Run.Scopes.Hash);
                }

                switch (Node.Step.EffectiveShape)
                {
                    case StepShape.Map:
                        await RunMapAsync(sources, token);
                        break;
                    case StepShape.Reduce:
                        await RunReduceAsync(sources, token);
                        break;
                    case StepShape.Group:
                        await RunGroupAsync(sources, token);
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                ReportFailure(BuildError.FromException(Name, null, e));
            }
            finally
            {
                ContributionReady.TrySetResult();

                if (initialized)
                {
                    try
                    {
                        await Node.Step.DisposeAsync();
                    }
                    catch (Exception e)
                    {
                        Run.AddError(BuildError.FromException(Name, null, e));
                    }
                }

                await CompleteAsync(token);
            }
        }

        /// <summary>
        /// Makes the run final, then reports it; <see cref="RunAsync"/> then completes the channel. The needs of the node
        /// are reported first: a node that stopped early (a failure, a cancellation) waits for them, so that for every
        /// edge A -> B the view, the trace and the result see A complete before B. Waiting is safe: a need waits on this
        /// node only for room in its channel, which <see cref="DrainInputs"/> makes.
        /// </summary>
        private async Task CompleteAsync(CancellationToken token)
        {
            // Before waiting for the needs, which may be waiting for room in the channels of this node.
            DrainInputs();

            MarkStarted();
            Run.EndedAt = DateTimeOffset.UtcNow;

            await Task.WhenAll(Node.Needs.Select(x => _runners[x.Index].Completed.Task));

            Run.State = GetFinalState(token);
            Run.Seal();

            // The state is final before the span ends: the timeline colors failed steps.
            _span.AddArg("files", Run.FilesConsumed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _span.AddArg("state", Run.State.ToString().ToLowerInvariant());
            _span.Dispose();

            Context.View.StepCompleted(Run);

            // RunAsync completes the channel and Completed.
            _stepCts.Dispose();
        }

        /// <summary>
        /// Starts the step's time and span when it has something to do: its first entering file, or its first unit (a
        /// reduce step with no input still runs once). A step that waits for its needs is not running yet, so the
        /// times of the summary and the trace show when each step worked.
        /// </summary>
        private void MarkStarted()
        {
            if (Volatile.Read(ref _started) == 1)
            {
                return;
            }

            lock (_startLock)
            {
                if (_started == 1)
                {
                    return;
                }

                Run.StartedAt = DateTimeOffset.UtcNow;
                _span = Context.Trace.Begin(Name, "step");
                _span.AddArg("shape", Node.Step.EffectiveShape.ToString().ToLowerInvariant());
                Volatile.Write(ref _started, 1);
            }

            Context.View.StepStarted(Run);
        }

        /// <summary>
        /// Called once every need is complete.
        /// </summary>
        private StepState GetFinalState(CancellationToken token)
        {
            if (Run.Errors.Count > 0)
            {
                return StepState.Failed;
            }

            if (!_upstreamUnavailable && !token.IsCancellationRequested)
            {
                return StepState.Succeeded;
            }

            // The node stopped early: it is skipped when a need failed or was skipped, cancelled otherwise.
            return Node.Needs.Any(x => _runners[x.Index].Run.State is StepState.Failed or StepState.Skipped)
                ? StepState.Skipped
                : StepState.Cancelled;
        }

        /// <summary>
        /// Records a failure and applies the failure policy.
        /// </summary>
        private void ReportFailure(BuildError error)
        {
            Run.AddError(error);

            switch (Context.Options.Failure)
            {
                case FailurePolicy.Default:
                    _stepCts.Cancel();
                    break;
                case FailurePolicy.FailFast:
                    _buildCts.Cancel();
                    break;
                case FailurePolicy.KeepGoing:
                    break;
            }
        }

        /// <summary>
        /// Reads the entering files (matching source files and the outputs of every need), detects paths coming from two
        /// producers, passes through files the step does not match, and hands the others to <paramref name="consume"/>.
        /// </summary>
        private async Task ReadInputsAsync(SourceFiles sources, Func<VirtualFile, ValueTask> consume, CancellationToken token)
        {
            var pumps = new List<Task>(Node.Needs.Count + 1);

            if (Node.Inputs != null)
            {
                pumps.Add(Task.Run(async () =>
                {
                    try
                    {
                        await AcceptAllAsync(sources.FilesOf(Node).ToAsyncEnumerable(), Provenance.SourceStep, consume, token);
                    }
                    finally
                    {
                        ReleaseSources(sources);
                    }
                }, token));
            }

            foreach (var need in Node.Needs)
            {
                var reader = _runners[need.Index].Output.Subscribe(Name);
                var producer = need.Definition.Name;

                pumps.Add(Task.Run(async () =>
                {
                    try
                    {
                        await AcceptAllAsync(reader.ReadAllAsync(token), producer, consume, token);
                    }
                    catch (Exception e) when (e is StepUnavailableException or ChannelClosedException { InnerException: StepUnavailableException })
                    {
                        // A need did not succeed: stop now, the final state is decided from the needs' states.
                        _upstreamUnavailable = true;
                        _stepCts.Cancel();
                    }
                }, token));
            }

            await Task.WhenAll(pumps);

            token.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Accepts the files of one producer in order, or, when the node tracks paths (several producers), several at once:
        /// <see cref="EnterOnceAsync"/> hashes every file, which must not run one file at a time.
        /// </summary>
        private async Task AcceptAllAsync(IAsyncEnumerable<VirtualFile> files, string producer, Func<VirtualFile, ValueTask> consume, CancellationToken token)
        {
            if (_producers == null)
            {
                await foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    await AcceptAsync(file, producer, consume, token);
                }

                return;
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism, CancellationToken = token };
            await Parallel.ForEachAsync(files, options, (file, ct) => AcceptAsync(file, producer, consume, ct));
        }

        private async ValueTask AcceptAsync(VirtualFile file, string producer, Func<VirtualFile, ValueTask> consume, CancellationToken token)
        {
            if (_producers != null && !await EnterOnceAsync(file, producer, token))
            {
                return;
            }

            MarkStarted();
            Run.AddFileIn();

            if (Node.Match.IsMatch(file.Path))
            {
                Run.AddConsumed();
                await consume(file);
            }
            else
            {
                // Not consumed: passed through at once, without waiting for the step.
                Run.AddPassedThrough();
                await Output.WriteAsync(file, token);
            }
        }

        /// <summary>
        /// Whether a file arriving from <paramref name="producer"/> enters the node: a path that another producer already
        /// delivered enters once when it is the same file (passed through two needs, a diamond in the graph, or an equal
        /// instance: a replay re-emitting an unchanged file, a step returning an equal copy), and is an error otherwise.
        /// Only the refs are kept: keeping the files would keep every content that entered the node until the build ends.
        /// </summary>
        private async ValueTask<bool> EnterOnceAsync(VirtualFile file, string producer, CancellationToken token)
        {
            await file.Content.EnsureHashAsync(token);
            var reference = file.Ref;
            (string Producer, FileRef Ref) existing;

            lock (_pathsLock)
            {
                if (!_producers!.TryGetValue(file.Path, out existing) || existing.Producer == producer)
                {
                    _producers[file.Path] = (producer, reference);
                    return true;
                }
            }

            if (existing.Ref != reference)
            {
                var (first, second) = string.CompareOrdinal(existing.Producer, producer) < 0 ? (existing.Producer, producer) : (producer, existing.Producer);
                ReportFailure(new BuildError(Name, file.Path, $"step '{Name}' received '{file.Path}' from both '{first}' and '{second}'", null, null));
            }

            return false;
        }

        private async Task RunMapAsync(SourceFiles sources, CancellationToken token)
        {
            var step = (IMapStep)Node.Step;
            // Bounded like the channels of the needs: the files a map step accepted wait for a unit here.
            var work = Channel.CreateBounded<VirtualFile>(new BoundedChannelOptions(Context.MaxDegreeOfParallelism) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

            var reading = Task.Run(async () =>
            {
                try
                {
                    await ReadInputsAsync(sources, file =>
                    {
                        Run.AddUnits(1);
                        return work.Writer.WriteAsync(file, token);
                    }, token);
                }
                finally
                {
                    Run.UnitsFinal = true;
                    work.Writer.TryComplete();
                }
            }, token);

            var options = new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism, CancellationToken = token };
            var variants = Node.Step.RecordsOwnReads ? new VariantSharing() : null;

            var processing = Parallel.ForEachAsync(work.Reader.ReadAllAsync(token), options, async (file, ct) =>
            {
                // Loaded before the slot is taken when its hash is not known: the read overlaps with the units that
                // run, and the hash of the input ref never blocks a thread. Content whose hash is known (from the
                // cache) is read only if the unit runs.
                await file.Content.EnsureHashAsync(ct);

                if (variants != null && file.Variant != null && VariantSharing.KeyOf(file) is { } variantKey)
                {
                    await RunVariantAsync(variants, variantKey, file, Body, ct);
                }
                else
                {
                    await RunUnitAsync(new UnitKey(Name, file.Path.FullName), [file], [file.Ref], Body(file), file.Path, ct);
                }
            });

            Func<StepContext, CancellationToken, ValueTask<StepOutput>> Body(VirtualFile file) => async (context, ct) =>
            {
                var outputs = await step.ProcessAsync(file, context, ct);
                return new StepOutput(outputs);
            };

            try
            {
                await processing;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Units report their own failures: this one is the step's, which stops reading its inputs too.
                _stepCts.Cancel();
                throw;
            }
            finally
            {
                await reading.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            // Surfaces a failure to read the inputs once the processing is done.
            await reading;
        }

        private async Task RunReduceAsync(SourceFiles sources, CancellationToken token)
        {
            var step = (IReduceStep)Node.Step;
            var files = await ReadAllInputsAsync(sources, token);

            Run.AddUnits(1);
            Run.UnitsFinal = true;

            // RunUnitAsync publishes the contribution before it writes the outputs.
            await RunUnitAsync(new UnitKey(Name, UnitKey.ReduceUnit), files, Refs(files), (context, ct) => step.ProcessAllAsync(files, context, ct), null, token);
        }

        private async Task RunGroupAsync(SourceFiles sources, CancellationToken token)
        {
            var step = (IGroupStep)Node.Step;
            var files = await ReadAllInputsAsync(sources, token);

            // Files are sorted, so every group's files are sorted too.
            var resolver = new GroupKeyResolver(Node.Definition.GroupBy!, Context, Name);
            var partition = new DependencyTracker();
            var groups = new Dictionary<string, List<VirtualFile>>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                string key;

                try
                {
                    key = await resolver.GetKeyAsync(file, Run.Scopes, partition, token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ReportFailure(BuildError.FromException(Name, file.Path, e));
                    continue;
                }

                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = [];
                }

                group.Add(file);
            }

            var ordered = groups.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new FileGroup(x.Key, x.Value)).ToArray();
            var partitionDependencies = partition.ToSet();

            Run.AddUnits(ordered.Length);
            Run.UnitsFinal = true;

            var options = new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism, CancellationToken = token };

            await Parallel.ForEachAsync(ordered, options, async (group, ct) =>
            {
                await RunUnitAsync(new UnitKey(Name, group.Key), group.Files, Refs(group.Files), (context, ct) =>
                {
                    // The partition depends on what the group keys read.
                    context.Dependencies.Add(partitionDependencies);

                    return step.ProcessGroupAsync(group, context, ct);
                }, null, ct);
            });
        }

        /// <summary>
        /// The refs of the inputs of a unit. The hashes of many files are computed in parallel: a reduce step's input is
        /// often the whole site, whose metadata nobody hashed yet.
        /// </summary>
        private ImmutableArray<FileRef> Refs(IReadOnlyList<VirtualFile> files)
        {
            var refs = new FileRef[files.Count];

            if (files.Count < 64)
            {
                for (var i = 0; i < refs.Length; i++)
                {
                    refs[i] = files[i].Ref;
                }
            }
            else
            {
                Parallel.For(0, refs.Length, new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism }, i => refs[i] = files[i].Ref);
            }

            return ImmutableCollectionsMarshal.AsImmutableArray(refs);
        }

        /// <summary>
        /// Reads the whole input (the barrier of reduce and group steps), sorted by path, with every content loaded.
        /// </summary>
        private async Task<List<VirtualFile>> ReadAllInputsAsync(SourceFiles sources, CancellationToken token)
        {
            var files = new List<VirtualFile>();
            var filesLock = new Lock();

            await ReadInputsAsync(sources, file =>
            {
                // Waits for the other inputs.
                file.Content.Compact();

                lock (filesLock)
                {
                    files.Add(file);
                }

                return ValueTask.CompletedTask;
            }, token);

            files.Sort(static (a, b) => a.Path.CompareTo(b.Path));

            // Source files that no map step read yet (the data step's inputs) are loaded in parallel, to be hashed.
            var options = new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism, CancellationToken = token };
            await Parallel.ForEachAsync(files, options, static (file, ct) => file.Content.EnsureHashAsync(ct));

            return files;
        }

        /// <summary>
        /// Runs one unit under a work slot: creates its dependency tracker and span, and records its result; then, with
        /// the slot released, writes its outputs to the channel. Failures are reported, not thrown. With the cache, a
        /// unit whose result key has a result whose dependencies did not change is replayed instead, without a slot: its
        /// outputs (and its contribution) come from the cache. A unit that runs stores its result.
        /// </summary>
        private async Task RunUnitAsync(UnitKey key, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, CancellationToken ct)
        {
            MarkStarted();

            if (await ExecuteUnitAsync(key, inputFiles, inputs, body, file, null, ct) is { } outcome)
            {
                await EmitAsync(outcome.Outputs, ct);
            }
        }

        /// <summary>
        /// The part of <see cref="RunUnitAsync"/> that replays or runs the unit. Returns null when the unit is done
        /// without outputs to write (it failed or was cancelled). <paramref name="subject"/>: the metadata whose own
        /// reads are recorded (<see cref="DependencyTracker.Subject"/>).
        /// </summary>
        private async Task<UnitOutcome?> ExecuteUnitAsync(UnitKey key, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, Metadata? subject, CancellationToken ct)
        {
            var replay = _replay;
            var resultKey = replay == null ? default : ComputeResultKey(inputFiles, inputs);

            // A replay takes no slot: it checks dependencies and restores outputs, which is short, and a rebuild replays
            // nearly every unit.
            if (replay != null && await TryReplayAsync(replay, resultKey, key, inputFiles, inputs, file, ct) is (true, var replayed))
            {
                return replayed is { } files ? new UnitOutcome(files, null, DependencySet.Empty) : null;
            }

            var slots = Node.Definition.IsCommand ? Context.ProcessSlots : Context.Parallelism;
            await slots.WaitAsync(ct);

            try
            {
                return await ProcessUnitAsync(key, resultKey, inputFiles, inputs, body, file, subject, ct);
            }
            finally
            {
                slots.Release();
            }
        }

        private Hash128 ComputeResultKey(IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs)
        {
            return ResultKey.Compute(Node.Definition, inputs, _version, Node.Step.ReadsContent, [.. inputFiles.Select(x => x.LastWriteTimeUtc)], [.. inputFiles.Select(x => x.SourcePath)]);
        }

        /// <summary>
        /// Publishes the outputs of a unit: the contribution of a reduce step, then the files, to the channel.
        /// </summary>
        private async Task EmitAsync(FileList files, CancellationToken ct)
        {
            // Dependents start with the contribution of a reduce step, so it is published before its outputs: a
            // dependent may have to read them to make room in its channel.
            if (Node.Step.EffectiveShape == StepShape.Reduce)
            {
                ContributionReady.TrySetResult();
            }

            // Without the slot: a full channel waits for its consumer, whose units need slots to empty it.
            for (var i = 0; i < files.Count; i++)
            {
                await Output.WriteAsync(files[i], ct);
            }

            Run.AddUnitDone();
            Context.View.StepProgress(Run);
        }

        /// <summary>
        /// The part of <see cref="RunUnitAsync"/> under the slot. Returns the outputs to write, with what the unit read,
        /// or null when the unit is done without outputs to write (it failed or was cancelled).
        /// </summary>
        private async ValueTask<UnitOutcome?> ProcessUnitAsync(UnitKey key, Hash128 resultKey, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, Metadata? subject, CancellationToken ct)
        {
            var replay = _replay;

            var deps = new DependencyTracker { Subject = subject };
            using var span = Context.Trace.Begin(Name, UnitCategory, key.Unit);
            var context = new StepContext(Node.Definition, Run.Scopes, Context, deps, span);

            StepOutput output;
            ScopeSet? contributions;

            try
            {
                output = WithAddedOutputs(await body(context, ct), context, inputs);
                contributions = Contribute(output);
                CheckContents(output, inputFiles);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e)
            {
                ReportFailure(BuildError.FromException(Name, file, e));
                Run.AddUnitDone();
                Context.View.StepProgress(Run);
                return null;
            }

            var files = output.Files;
            Run.AddOut(files.Count);

            var dependencies = deps.ToSet();

            if (replay != null)
            {
                dependencies = replay.Cache.Intern(dependencies);
                await StoreAsync(replay, resultKey, key, inputFiles, files, dependencies, contributions, ct);
            }
            else if (!dependencies.IsEmpty)
            {
                dependencies = _scheduler._dependencySets.GetOrAdd(dependencies.Hash, dependencies);
            }

            Run.AddResult(new UnitResult(key, inputs, [.. files.AsEnumerable().Select(x => x.Ref)], dependencies)
            {
                Files = Context.Options.KeepFiles ? [.. files.AsEnumerable()] : [],
            });

            return new UnitOutcome(files, subject == null ? null : deps.GetOwnReads(), dependencies);
        }

        /// <summary>
        /// Runs the unit of a file that has variants (<see cref="FileVariant"/>). The first variant of a group to arrive
        /// runs; the others wait for its outcome without holding a slot nor a degree of the step, and take its result
        /// when it did not read what makes them differ (<see cref="VariantSharing.Share"/>), or run their own unit.
        /// </summary>
        private async Task RunVariantAsync(VariantSharing variants, Hash128 variantKey, VirtualFile file, Func<VirtualFile, Func<StepContext, CancellationToken, ValueTask<StepOutput>>> body, CancellationToken ct)
        {
            MarkStarted();

            if (!variants.Enter(variantKey, file, out var group))
            {
                // Parked, or the leader is done: the leader runs the parked followers.
                if (group.TryFollow(file, out var leaderOutcome))
                {
                    await FollowAsync(group.Leader, leaderOutcome, file, body, ct);
                }

                return;
            }

            UnitOutcome? outcome;

            try
            {
                outcome = await ExecuteUnitAsync(new UnitKey(Name, file.Path.FullName), [file], [file.Ref], body(file), file.Path, file.Metadata, ct);
            }
            catch
            {
                // Cancelled: the parked followers are dropped with the step.
                group.Complete(null);
                throw;
            }

            var parked = group.Complete(outcome);
            var followers = parked.Count == 0 ? Task.CompletedTask : Task.WhenAll(parked.Select(x => FollowAsync(file, outcome, x, body, ct)));

            try
            {
                if (outcome != null)
                {
                    await EmitAsync(outcome.Outputs, ct);
                }
            }
            finally
            {
                await followers;
            }
        }

        /// <summary>
        /// A follower of a group of variants: takes the result of the leader's unit when it can, runs its own otherwise.
        /// </summary>
        private async Task FollowAsync(VirtualFile leader, UnitOutcome? outcome, VirtualFile file, Func<VirtualFile, Func<StepContext, CancellationToken, ValueTask<StepOutput>>> body, CancellationToken ct)
        {
            var key = new UnitKey(Name, file.Path.FullName);

            if (outcome is { Reads: { } reads } && VariantSharing.Share(Name, leader, outcome.Outputs, reads, file) is { } shared)
            {
                using (var span = Context.Trace.Begin(Name, UnitCategory, key.Unit))
                {
                    span.AddArg("shared", "true");
                }

                Run.AddOut(shared.Count);
                Run.AddResult(new UnitResult(key, [file.Ref], [.. shared.AsEnumerable().Select(x => x.Ref)], outcome.Dependencies)
                {
                    Files = Context.Options.KeepFiles ? [.. shared.AsEnumerable()] : [],
                    Shared = true,
                });
                Run.AddUnitShared();

                if (_replay is { } replay)
                {
                    await StoreAsync(replay, ComputeResultKey([file], [file.Ref]), key, [file], shared, outcome.Dependencies, null, ct);
                }

                await EmitAsync(shared, ct);
                return;
            }

            await RunUnitAsync(key, [file], [file.Ref], body(file), file.Path, ct);
        }

        /// <summary>
        /// Appends the files the unit added with <see cref="StepContext.AddOutput"/> (a tag that produced an image) to the
        /// files its step returned, with the unit's provenance (its step and inputs, as a replay restores them). They are
        /// outputs like the others: stored with the result, replayed with it.
        /// </summary>
        private StepOutput WithAddedOutputs(StepOutput output, StepContext context, ImmutableArray<FileRef> inputs)
        {
            var added = context.AddedOutputs;

            if (added.Count == 0)
            {
                return output;
            }

            var provenance = new Provenance(Name, inputs);
            var files = new List<VirtualFile>(output.Files.Count + added.Count);
            files.AddRange(output.Files.AsEnumerable());

            foreach (var file in added)
            {
                files.Add(new VirtualFile(file.Path, file.Content, file.Metadata, file.ContentType, provenance, file.LastWriteTimeUtc, file.SourcePath));
            }

            return output with { Files = FileList.Of(files) };
        }

        /// <summary>
        /// A step that does not read the contents of its inputs (<see cref="Step.ReadsContent"/>) leaves them out of its
        /// result keys, so a unit is replayed after a content changed: an output with the content of an input would then
        /// be restored with the old content. Such a step cannot pass contents through.
        /// </summary>
        private void CheckContents(StepOutput output, IReadOnlyList<VirtualFile> inputFiles)
        {
            if (Node.Step.ReadsContent || output.Files.Count == 0)
            {
                return;
            }

            var files = output.Files;

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];

                if (inputFiles.Any(x => x.ContentHash == file.ContentHash))
                {
                    throw new InvalidOperationException($"step '{Name}' does not read contents but emits '{file.Path}' with the content of an input");
                }
            }
        }

        /// <summary>
        /// The contribution of a reduce unit, published as the step's; a group unit cannot contribute.
        /// </summary>
        private ScopeSet? Contribute(StepOutput output)
        {
            if (Node.Step.EffectiveShape != StepShape.Reduce)
            {
                if (output.Scope != null || output.Folders != null)
                {
                    throw new InvalidOperationException($"step '{Name}' is a group step and cannot contribute scopes");
                }

                return null;
            }

            var contributions = ScopeSet.Empty.WithFolders(output.Folders ?? []);

            if (output.Scope != null)
            {
                contributions = contributions.WithSite(output.Scope);
            }

            Run.Contributions = contributions;
            return contributions;
        }

        /// <summary>
        /// Replays a unit from the cache when its result key has a result whose dependencies did not change. Returns
        /// whether it did, with the outputs to write, or null when the replay failed (the unit is then done).
        /// </summary>
        private async ValueTask<(bool Replayed, FileList? Outputs)> TryReplayAsync(ReplayState replay, Hash128 resultKey, UnitKey key, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, FPath? file, CancellationToken ct)
        {
            if (!replay.Cache.TryGetUnit(resultKey, out var cached) || cached.Step != Name || cached.Unit != key.Unit)
            {
                return (false, null);
            }

            var dependencies = replay.Cache.GetDependencies(cached);

            if (!await replay.IsUnchangedAsync(Name, Run.Scopes, dependencies, ct))
            {
                return (false, null);
            }

            using var span = Context.Trace.Begin(Name, UnitCategory, key.Unit);
            span.AddArg("cached", "true");

            var provenance = new Provenance(Name, inputs);
            var outputs = new VirtualFile[cached.Outputs.Length];

            // Each output is matched against the inputs: indexed when both are many, scanned otherwise.
            var index = inputFiles.Count > 16 && outputs.Length > 16 ? new BuildCache.RestoreIndex(inputFiles) : null;

            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = index != null ? replay.Cache.Restore(cached.Outputs[i], index, provenance) : replay.Cache.Restore(cached.Outputs[i], inputFiles, provenance);
            }

            try
            {
                if (cached.Scope is { } scope && Node.Step.EffectiveShape == StepShape.Reduce)
                {
                    Run.Contributions = replay.Cache.RestoreScope(scope, inputFiles);
                }

                (Node.Step as IReplayObserver)?.Replayed(inputFiles, outputs);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ReportFailure(BuildError.FromException(Name, file, e));
                Run.AddUnitDone();
                Context.View.StepProgress(Run);
                return (true, null);
            }

            Run.AddOut(outputs.Length);
            replay.Cache.Reused(resultKey, cached);

            Run.AddResult(new UnitResult(key, inputs, [.. outputs.Select(x => x.Ref)], dependencies)
            {
                Files = Context.Options.KeepFiles ? [.. outputs] : [],
                Replayed = true,
            });

            Run.AddUnitReplayed();
            return (true, FileList.Of(outputs));
        }

        /// <summary>
        /// Stores the result of a unit that ran. A failure to write the cache is a warning: the build goes on.
        /// </summary>
        private async ValueTask StoreAsync(ReplayState replay, Hash128 resultKey, UnitKey key, IReadOnlyList<VirtualFile> inputFiles, FileList files, DependencySet dependencies, ScopeSet? contributions, CancellationToken ct)
        {
            try
            {
                var outputs = ImmutableArray.CreateBuilder<CachedOutput>(files.Count);

                for (var i = 0; i < files.Count; i++)
                {
                    outputs.Add(await replay.Cache.StoreOutputAsync(files[i], inputFiles, ct));
                }

                var scope = contributions == null ? null : replay.Cache.StoreScope(contributions);
                replay.Cache.Put(resultKey, new CachedUnit(Name, key.Unit, dependencies.Hash, outputs.MoveToImmutable(), scope), dependencies);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Context.View.Message(Progress.LogLevel.Warning, Name, $"cannot write the cache: {e.Message}");
            }
        }
    }
}
