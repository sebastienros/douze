using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Douze.FileSystems;
using Douze.Tracing;
using Douze.Workflow.Cache;

namespace Douze.Workflow.Scheduling;

internal sealed record SchedulerResult(IReadOnlyList<StepRun> Runs, IReadOnlyList<BuildError> Errors, ScopeSet FinalScopes, bool Cancelled);

/// <summary>
/// Runs a workflow graph: streams source files into root steps, runs each step according to its shape, merges scope
/// contributions, and applies the failure policy.
/// </summary>
internal sealed class Scheduler
{
    private const string UnitCategory = "unit";

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

        var sources = new SourceFiles(await EnumerateSourcesAsync(), _graph.Nodes.Count(x => x.Inputs != null));

        await Task.WhenAll(runners.Select(x => x.RunAsync(sources)));

        var runs = runners.Select(x => x.Run).ToArray();
        var errors = runs.SelectMany(x => x.Errors).ToArray();

        // Nearest contributions first: descending topological index.
        var finalScopes = ScopeSet.Merge(runs.Reverse().Select(x => x.Contributions).ToArray(), _context.BaseScopes);

        return new SchedulerResult(runs, errors, finalScopes, ct.IsCancellationRequested);
    }

    /// <summary>
    /// Enumerates the source files once, sorted by path, keeping only those that some node's <c>inputs</c> match.
    /// Files under the output folder or the cache folder are excluded when they are inside the source folder. A file
    /// whose length and modification time did not change since a build that cached its hash is not read to be hashed.
    /// </summary>
    private async Task<IReadOnlyList<VirtualFile>> EnumerateSourcesAsync()
    {
        var roots = _graph.Nodes.Where(x => x.Inputs != null).Select(x => x.Inputs!).ToArray();

        if (roots.Length == 0)
        {
            return [];
        }

        var source = _context.Source;
        var excluded = _context.ExcludedSourceFolders;
        var entries = await source.EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true, filter: null);

        var paths = entries
            .Where(x => !x.IsDirectory)
            .Select(x => x.Path.ToAbsolute())
            .Where(x => !excluded.Any(x.IsInDirectory))
            .Where(x => roots.Any(r => r.IsMatch(x)))
            .Distinct()
            .Order()
            .ToArray();

        var files = new VirtualFile[paths.Length];
        var cache = _context.Cache;
        var options = new ParallelOptions { MaxDegreeOfParallelism = _context.MaxDegreeOfParallelism };

        await Parallel.ForAsync(0, paths.Length, options, async (i, _) =>
        {
            var path = paths[i];
            var info = await source.GetFileMetadataAsync(path);
            var (length, date) = (info.Length, info.LastWriteTimeUtc);
            FileContent content;

            if (cache == null)
            {
                content = FileContent.FromFile(source, path, length);
            }
            else if (cache.TryGetSourceHash(path, length, date, out var hash))
            {
                cache.PutSource(path, length, date, hash);
                content = FileContent.FromFile(source, path, length, hash, null);
            }
            else
            {
                content = FileContent.FromFile(source, path, length, null, hash => cache.PutSource(path, length, date, hash));
            }

            files[i] = VirtualFile.FromSource(path, content, date);
        });

        return files;
    }

    /// <summary>
    /// The source files, released once every node with <c>inputs</c> has read them, so that their contents do not stay
    /// in memory until the build ends.
    /// </summary>
    private sealed class SourceFiles(IReadOnlyList<VirtualFile> files, int readers)
    {
        private IReadOnlyList<VirtualFile>? _files = files;
        private int _readers = readers;

        public IReadOnlyList<VirtualFile> Files => _files ?? [];

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
        private readonly Dictionary<FPath, (string Producer, VirtualFile File)> _producers = new();
        private readonly Lock _startLock = new();
        private int _started;
        private TraceSpan _span;
        private bool _upstreamUnavailable;
        private int _sourcesReleased;

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
            Output = new FileChannel(node.Dependents.Select(x => x.Definition.Name));

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
                // A step sees the contributions of every ancestor, nearest first (descending topological index).
                var ancestors = _scheduler._graph.Ancestors(Name).Select(x => _runners[_scheduler._graph[x].Index]).OrderByDescending(x => x.Node.Index).ToArray();
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
        /// edge A -> B the view, the trace and the result see A complete before B. Waiting is safe: needs never wait on
        /// their dependents, and writes to the channels of a node that stopped never block.
        /// </summary>
        private async Task CompleteAsync(CancellationToken token)
        {
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
        private async Task ReadInputsAsync(SourceFiles sources, Action<VirtualFile> consume, CancellationToken token)
        {
            var pumps = new List<Task>(Node.Needs.Count + 1);

            if (Node.Inputs is { } inputs)
            {
                pumps.Add(Task.Run(async () =>
                {
                    try
                    {
                        foreach (var file in sources.Files)
                        {
                            token.ThrowIfCancellationRequested();

                            if (inputs.IsMatch(file.Path))
                            {
                                await AcceptAsync(file, Provenance.SourceStep, consume, token);
                            }
                        }
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
                        await foreach (var file in reader.ReadAllAsync(token))
                        {
                            await AcceptAsync(file, producer, consume, token);
                        }
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

        private async ValueTask AcceptAsync(VirtualFile file, string producer, Action<VirtualFile> consume, CancellationToken token)
        {
            (string Producer, VirtualFile File)? conflict = null;

            lock (_pathsLock)
            {
                if (_producers.TryGetValue(file.Path, out var existing) && existing.Producer != producer)
                {
                    // The same file passed through two needs (a diamond in the graph) enters once.
                    if (ReferenceEquals(existing.File, file))
                    {
                        return;
                    }

                    conflict = existing;
                }
                else
                {
                    _producers[file.Path] = (producer, file);
                }
            }

            if (conflict is { } other)
            {
                // Another instance of the same file (a replay re-emitting an unchanged file, a step returning an equal
                // copy) enters once too; two different files with the same path are an error. The refs are compared
                // outside the lock: hashing loads the contents.
                await file.Content.EnsureHashAsync(token);
                await other.File.Content.EnsureHashAsync(token);

                if (other.File.Ref == file.Ref)
                {
                    return;
                }

                var (first, second) = string.CompareOrdinal(other.Producer, producer) < 0 ? (other.Producer, producer) : (producer, other.Producer);
                ReportFailure(new BuildError(Name, file.Path, $"step '{Name}' received '{file.Path}' from both '{first}' and '{second}'", null, null));
                return;
            }

            MarkStarted();
            Run.AddFileIn();

            if (Node.Match.IsMatch(file.Path))
            {
                Run.AddConsumed();
                consume(file);
            }
            else
            {
                // Not consumed: passed through at once, without waiting for the step.
                Run.AddPassedThrough();
                await Output.WriteAsync(file, token);
            }
        }

        private async Task RunMapAsync(SourceFiles sources, CancellationToken token)
        {
            var step = (IMapStep)Node.Step;
            var work = Channel.CreateUnbounded<VirtualFile>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

            var reading = Task.Run(async () =>
            {
                try
                {
                    await ReadInputsAsync(sources, file =>
                    {
                        Run.AddUnits(1);
                        work.Writer.TryWrite(file);
                    }, token);
                }
                finally
                {
                    Run.UnitsFinal = true;
                    work.Writer.TryComplete();
                }
            }, token);

            var options = new ParallelOptions { MaxDegreeOfParallelism = Context.MaxDegreeOfParallelism, CancellationToken = token };

            var processing = Parallel.ForEachAsync(work.Reader.ReadAllAsync(token), options, async (file, ct) =>
            {
                // Loaded before the slot is taken when its hash is not known: the read overlaps with the units that
                // run, and the hash of the input ref never blocks a thread. Content whose hash is known (from the
                // cache) is read only if the unit runs.
                await file.Content.EnsureHashAsync(ct);

                await RunUnitAsync(new UnitKey(Name, file.Path.FullName), [file], [file.Ref], async (context, ct) =>
                {
                    var outputs = await step.ProcessAsync(file, context, ct);
                    return new StepOutput(outputs);
                }, file.Path, ct);
            });

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
                lock (filesLock)
                {
                    files.Add(file);
                }
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
        /// unit whose result key has a result whose dependencies did not change is replayed instead: its outputs (and
        /// its contribution) come from the cache. A unit that runs stores its result.
        /// </summary>
        private async Task RunUnitAsync(UnitKey key, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, CancellationToken ct)
        {
            MarkStarted();

            var slots = Node.Definition.IsCommand ? Context.ProcessSlots : Context.Parallelism;
            await slots.WaitAsync(ct);

            FileList? outputs;

            try
            {
                outputs = await ProcessUnitAsync(key, inputFiles, inputs, body, file, ct);
            }
            finally
            {
                slots.Release();
            }

            if (outputs is not { } files)
            {
                return;
            }

            // Dependents start with the contribution of a reduce step: published as soon as it is known.
            if (Node.Step.EffectiveShape == StepShape.Reduce)
            {
                ContributionReady.TrySetResult();
            }

            // Written without the slot, which another unit can use meanwhile.
            for (var i = 0; i < files.Count; i++)
            {
                await Output.WriteAsync(files[i], ct);
            }

            Run.AddUnitDone();
            Context.View.StepProgress(Run);
        }

        /// <summary>
        /// The part of <see cref="RunUnitAsync"/> under the slot. Returns the outputs to write, or null when the unit is
        /// done without outputs to write (it failed or was cancelled).
        /// </summary>
        private async ValueTask<FileList?> ProcessUnitAsync(UnitKey key, IReadOnlyList<VirtualFile> inputFiles, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, CancellationToken ct)
        {
            var replay = _replay;
            var resultKey = replay == null ? default : ResultKey.Compute(Node.Definition, inputs, _version, Node.Step.ReadsContent, [.. inputFiles.Select(x => x.LastWriteTimeUtc)]);

            if (replay != null && await TryReplayAsync(replay, resultKey, key, inputFiles, inputs, file, ct) is (true, var replayed))
            {
                return replayed;
            }

            var deps = new DependencyTracker();
            using var span = Context.Trace.Begin(Name, UnitCategory, key.Unit);
            var context = new StepContext(Node.Definition, Run.Scopes, Context, deps, span);

            StepOutput output;
            ScopeSet? contributions;

            try
            {
                output = await body(context, ct);
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

            return files;
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

            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i] = replay.Cache.Restore(cached.Outputs[i], inputFiles, provenance);
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
