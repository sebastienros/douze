using System.Collections.Immutable;
using System.Threading.Channels;
using Douze.FileSystems;

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

    public Scheduler(WorkflowGraph graph, BuildContext context)
    {
        _graph = graph;
        _context = context;
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
        var finalScopes = ScopeSet.Merge(runs.Reverse().Select(x => x.Contributions).ToArray(), ScopeSet.Empty);

        return new SchedulerResult(runs, errors, finalScopes, ct.IsCancellationRequested);
    }

    /// <summary>
    /// Enumerates the source files once, sorted by path, keeping only those that some node's <c>inputs</c> match.
    /// Files under the output folder are excluded when it is inside the source folder.
    /// </summary>
    private async Task<IReadOnlyList<VirtualFile>> EnumerateSourcesAsync()
    {
        var roots = _graph.Nodes.Where(x => x.Inputs != null).Select(x => x.Inputs!).ToArray();

        if (roots.Length == 0)
        {
            return [];
        }

        var source = _context.Source;
        var excluded = GetExcludedOutputFolder();
        var entries = await source.EnumerateFileSystemEntriesAsync(FPath.Root, recursive: true, filter: null);

        var paths = entries
            .Where(x => !x.IsDirectory)
            .Select(x => x.Path.ToAbsolute())
            .Where(x => excluded is not { } folder || !x.IsInDirectory(folder))
            .Where(x => roots.Any(r => r.IsMatch(x)))
            .Distinct()
            .Order()
            .ToArray();

        var files = new VirtualFile[paths.Length];

        for (var i = 0; i < paths.Length; i++)
        {
            files[i] = VirtualFile.FromSource(source, paths[i], await source.GetFileMetadataAsync(paths[i]));
        }

        return files;
    }

    private FPath? GetExcludedOutputFolder()
    {
        if (_context.Site.SourceDirectory is not { } sourceDirectory || _context.Site.OutputDirectory is not { } outputDirectory)
        {
            return null;
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)) + Path.DirectorySeparatorChar;
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)) + Path.DirectorySeparatorChar;

        if (!outputRoot.StartsWith(sourceRoot, StringComparison.Ordinal) || outputRoot.Length == sourceRoot.Length)
        {
            return null;
        }

        return new FPath("/" + outputRoot[sourceRoot.Length..]);
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
        private readonly Dictionary<FPath, string> _producers = new();
        private bool _upstreamUnavailable;

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

        public async Task RunAsync(IReadOnlyList<VirtualFile> sources)
        {
            var token = _stepCts.Token;
            var initialized = false;

            try
            {
                // A step sees the contributions of every ancestor, nearest first (descending topological index).
                var ancestors = _scheduler._graph.Ancestors(Name).Select(x => _runners[_scheduler._graph[x].Index]).OrderByDescending(x => x.Node.Index).ToArray();
                await Task.WhenAll(ancestors.Select(x => x.ContributionReady.Task)).WaitAsync(token);
                Run.Scopes = ScopeSet.Merge(ancestors.Select(x => x.Run.Contributions).ToArray(), ScopeSet.Empty);

                Run.State = StepState.Running;
                Run.StartedAt = DateTimeOffset.UtcNow;
                Context.View.StepStarted(Run);

                using var span = Context.Trace.Begin(Name, "step");

                initialized = true;
                await Node.Step.InitializeAsync(Context, token);

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

                span.AddArg("files", Run.FilesConsumed.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        private async Task CompleteAsync(CancellationToken token)
        {
            Run.State = await GetFinalStateAsync(token);
            Run.StartedAt ??= DateTimeOffset.UtcNow;
            Run.EndedAt = DateTimeOffset.UtcNow;
            Run.Seal();

            Output.Complete(Run.State == StepState.Succeeded ? null : new StepUnavailableException(Name, Run.State));
            _stepCts.Dispose();

            Context.View.StepCompleted(Run);
            Completed.TrySetResult();
        }

        private async Task<StepState> GetFinalStateAsync(CancellationToken token)
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
            // Waiting is safe: needs never wait on their dependents, and they are stopping too.
            await Task.WhenAll(Node.Needs.Select(x => _runners[x.Index].Completed.Task));

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
        private async Task ReadInputsAsync(IReadOnlyList<VirtualFile> sources, Action<VirtualFile> consume, CancellationToken token)
        {
            var pumps = new List<Task>(Node.Needs.Count + 1);

            if (Node.Inputs is { } inputs)
            {
                pumps.Add(Task.Run(async () =>
                {
                    foreach (var file in sources)
                    {
                        token.ThrowIfCancellationRequested();

                        if (inputs.IsMatch(file.Path))
                        {
                            await AcceptAsync(file, Provenance.SourceStep, consume);
                        }
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
                            await AcceptAsync(file, producer, consume);
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

        private async ValueTask AcceptAsync(VirtualFile file, string producer, Action<VirtualFile> consume)
        {
            lock (_pathsLock)
            {
                if (_producers.TryGetValue(file.Path, out var existing) && existing != producer)
                {
                    var (first, second) = string.CompareOrdinal(existing, producer) < 0 ? (existing, producer) : (producer, existing);
                    ReportFailure(new BuildError(Name, file.Path, $"step '{Name}' received '{file.Path}' from both '{first}' and '{second}'", null, null));
                    return;
                }

                _producers[file.Path] = producer;
            }

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
                await Output.WriteAsync(file);
            }
        }

        private async Task RunMapAsync(IReadOnlyList<VirtualFile> sources, CancellationToken token)
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
                await RunUnitAsync(new UnitKey(Name, file.Path.FullName), [file.Ref], async (context, ct) =>
                {
                    var outputs = await step.ProcessAsync(file, context, ct);
                    return new StepOutput(outputs);
                }, file.Path, ct);
            });

            try
            {
                await processing;
            }
            finally
            {
                await reading.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            // Surfaces a failure to read the inputs once the processing is done.
            await reading;
        }

        private async Task RunReduceAsync(IReadOnlyList<VirtualFile> sources, CancellationToken token)
        {
            var step = (IReduceStep)Node.Step;
            var files = await ReadAllInputsAsync(sources, token);

            Run.AddUnits(1);
            Run.UnitsFinal = true;

            await RunUnitAsync(new UnitKey(Name, UnitKey.ReduceUnit), [.. files.Select(x => x.Ref)], async (context, ct) =>
            {
                var output = await step.ProcessAllAsync(files, context, ct);

                var contributions = ScopeSet.Empty.WithFolders(output.Folders ?? []);

                if (output.Scope != null)
                {
                    contributions = contributions.WithSite(output.Scope);
                }

                Run.Contributions = contributions;
                return output;
            }, null, token);

            // Published before the channel completes, so dependents see a complete scope set when they start.
            ContributionReady.TrySetResult();
        }

        private async Task RunGroupAsync(IReadOnlyList<VirtualFile> sources, CancellationToken token)
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
                await RunUnitAsync(new UnitKey(Name, group.Key), [.. group.Files.Select(x => x.Ref)], async (context, ct) =>
                {
                    // The partition depends on what the group keys read.
                    context.Dependencies.Add(partitionDependencies);

                    var output = await step.ProcessGroupAsync(group, context, ct);

                    if (output.Scope != null || output.Folders != null)
                    {
                        throw new InvalidOperationException($"step '{Name}' is a group step and cannot contribute scopes");
                    }

                    return output;
                }, null, ct);
            });
        }

        /// <summary>
        /// Reads the whole input (the barrier of reduce and group steps), sorted by path.
        /// </summary>
        private async Task<List<VirtualFile>> ReadAllInputsAsync(IReadOnlyList<VirtualFile> sources, CancellationToken token)
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
            return files;
        }

        /// <summary>
        /// Runs one unit under a work slot: creates its dependency tracker and span, writes its outputs to the channel as
        /// soon as it is done, and records its result. Failures are reported, not thrown.
        /// </summary>
        private async Task RunUnitAsync(UnitKey key, ImmutableArray<FileRef> inputs, Func<StepContext, CancellationToken, ValueTask<StepOutput>> body, FPath? file, CancellationToken ct)
        {
            var slots = Node.Definition.IsCommand ? Context.ProcessSlots : Context.Parallelism;
            await slots.WaitAsync(ct);

            try
            {
                var deps = new DependencyTracker();
                using var span = Context.Trace.Begin(Name, UnitCategory, key.Unit);
                var context = new StepContext(Node.Definition, Run.Scopes, Context, deps, span);

                StepOutput output;

                try
                {
                    output = await body(context, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    ReportFailure(BuildError.FromException(Name, file, e));
                    Run.AddUnitDone();
                    Context.View.StepProgress(Run);
                    return;
                }

                var files = output.Files;

                for (var i = 0; i < files.Count; i++)
                {
                    await Output.WriteAsync(files[i]);
                }

                Run.AddOut(files.Count);
                Run.AddResult(new UnitResult(key, inputs, [.. files.AsEnumerable()], deps.ToSet()));
            }
            finally
            {
                slots.Release();
            }

            Run.AddUnitDone();
            Context.View.StepProgress(Run);
        }
    }
}
