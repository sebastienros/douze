using System.Collections.Concurrent;
using System.Collections.Immutable;
using Douze.Progress;
using Douze.FileSystems;
using Douze.Tracing;
using Douze.Workflow;

namespace Douze.Tests.Workflow;

/// <summary>
/// A map step that, by default, appends "|&lt;name&gt;" to the content of every file it consumes.
/// Gates, delays and failures make scheduling observable.
/// </summary>
internal sealed class FakeMapStep : Step, IMapStep
{
    public FakeMapStep(StepDefinition definition) : base(definition)
    {
    }

    public Func<VirtualFile, StepContext, CancellationToken, ValueTask<FileList>>? Process { get; init; }

    /// <summary>Awaited before processing a file.</summary>
    public Func<VirtualFile, CancellationToken, Task>? Before { get; init; }

    public Func<VirtualFile, bool>? FailWhen { get; init; }

    public TimeSpan Delay { get; init; }

    public ConcurrencyCounter? Counter { get; init; }

    public GlobSet? Default { get; init; }

    /// <summary>Whether the step claims to read the contents of its inputs.</summary>
    public bool Contents { get; init; } = true;

    public override GlobSet DefaultMatch => Default ?? GlobSet.All;

    public override StepShape NativeShape => StepShape.Map;

    public override bool ReadsContent => Contents;

    public ConcurrentQueue<FPath> Started { get; } = new();

    public ConcurrentQueue<FPath> Done { get; } = new();

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        Started.Enqueue(file.Path);
        using var _ = Counter?.Enter();

        if (Before != null)
        {
            await Before(file, ct);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (FailWhen?.Invoke(file) == true)
        {
            throw new DouzeException($"failed on {file.Path}");
        }

        var result = Process != null
            ? await Process(file, context, ct)
            : FileList.Of(file.With(Name, content: FileContent.FromText(await file.Content.ReadTextAsync(ct) + "|" + Name)));

        Done.Enqueue(file.Path);
        return result;
    }
}

/// <summary>
/// A reduce step that records what it received and, optionally, contributes scopes or emits files.
/// </summary>
internal sealed class FakeReduceStep : Step, IReduceStep
{
    public FakeReduceStep(StepDefinition definition) : base(definition)
    {
    }

    public Func<IReadOnlyList<VirtualFile>, StepContext, StepOutput>? Reduce { get; init; }

    public Func<CancellationToken, Task>? Before { get; init; }

    public bool Fail { get; init; }

    public ConcurrencyCounter? Counter { get; init; }

    public override StepShape NativeShape => StepShape.Reduce;

    public IReadOnlyList<VirtualFile> Received { get; private set; } = [];

    public ScopeSet? SeenScopes { get; private set; }

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        using var _ = Counter?.Enter();

        Received = files;
        SeenScopes = context.Scopes;

        if (Before != null)
        {
            await Before(ct);
        }

        if (Fail)
        {
            throw new DouzeException("reduce failed");
        }

        return Reduce != null ? Reduce(files, context) : new StepOutput(FileList.None);
    }
}

/// <summary>
/// A group step that records its groups and concatenates the contents of each group into one file named after the key.
/// </summary>
internal sealed class FakeGroupStep : Step, IGroupStep
{
    public FakeGroupStep(StepDefinition definition) : base(definition)
    {
    }

    public override StepShape NativeShape => StepShape.Map;

    public override bool SupportsGrouping => true;

    public ConcurrentQueue<FileGroup> Groups { get; } = new();

    public async ValueTask<StepOutput> ProcessGroupAsync(FileGroup group, StepContext context, CancellationToken ct)
    {
        Groups.Enqueue(group);

        var contents = new List<string>();

        foreach (var file in group.Files)
        {
            contents.Add(await file.Content.ReadTextAsync(ct));
        }

        var path = new FPath("/groups/" + group.Key.Trim('/').Replace('/', '_') + ".txt");
        return new StepOutput(FileList.Of(VirtualFile.Derived(Name, group.Files, path, FileContent.FromText(string.Join(",", contents)), Metadata.Empty, null)));
    }
}

/// <summary>
/// Writes every file it consumes to the output file system and passes it through, like the built-in <c>write</c> step.
/// </summary>
internal sealed class FakeWriteStep : Step, IMapStep
{
    public FakeWriteStep(StepDefinition definition) : base(definition)
    {
    }

    public override StepShape NativeShape => StepShape.Map;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        await context.Build.Output.CreateDirectoryAsync(file.Path.GetDirectory());

        await using (var stream = await context.Build.Output.CreateFileAsync(file.Path))
        {
            await file.Content.CopyToAsync(stream, ct);
        }

        return FileList.Of(file);
    }
}

/// <summary>
/// Counts how many units run at once.
/// </summary>
internal sealed class ConcurrencyCounter
{
    private int _current;
    private int _max;

    public int Max => Volatile.Read(ref _max);

    public IDisposable Enter()
    {
        var current = Interlocked.Increment(ref _current);
        int max;

        while (current > (max = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, current, max) != max)
        {
        }

        return new Exit(this);
    }

    private sealed class Exit(ConcurrencyCounter counter) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref counter._current);
    }
}

/// <summary>
/// Builds a workflow of fake steps: each definition comes with the factory of its step.
/// </summary>
internal sealed class FakeWorkflow
{
    private readonly List<StepDefinition> _definitions = [];
    private readonly Dictionary<string, Func<StepDefinition, Step>> _factories = new(StringComparer.Ordinal);

    public FakeWorkflow Add(string name, Func<StepDefinition, Step> factory, string[]? needs = null, string[]? inputs = null, string[]? match = null, string? groupBy = null, string? uses = null)
    {
        _definitions.Add(new StepDefinition
        {
            Name = name,
            Needs = [.. needs ?? []],
            Inputs = [.. inputs ?? []],
            Match = [.. match ?? []],
            GroupBy = groupBy,
            Uses = uses ?? "fake",
        });

        _factories[name] = factory;
        return this;
    }

    public FakeWorkflow Write(string name, params string[] needs)
    {
        return Add(name, d => new FakeWriteStep(d), needs: needs, uses: "write");
    }

    public WorkflowDefinition Definition => new([.. _definitions], null);

    public WorkflowGraph Build() => WorkflowGraph.Build(Definition, d => _factories[d.Name](d));
}

/// <summary>
/// A source and an output <see cref="MemoryFileSystem"/> and helpers to build a graph between them.
/// </summary>
internal sealed class FakeSite
{
    public MemoryFileSystem Source { get; } = new();

    public MemoryFileSystem Output { get; } = new();

    public SiteOptions Options => new() { Source = Source, Output = Output };

    public async Task<FakeSite> WithFilesAsync(params string[] paths)
    {
        foreach (var path in paths)
        {
            await Source.WriteTextFileAsync(path, path);
        }

        return this;
    }

    public Task<BuildResult> BuildAsync(WorkflowGraph graph, FailurePolicy failure = FailurePolicy.Default, int parallelism = 4, CancellationToken? cancellation = null, IProgressView? view = null, ITraceRecorder? trace = null, bool keepFiles = true)
    {
        return SiteBuilder.BuildAsync(graph, Options, new BuildOptions
        {
            MaxDegreeOfParallelism = parallelism,
            KeepFiles = keepFiles,
            Failure = failure,
            Cancellation = cancellation ?? Xunit.TestContext.Current.CancellationToken,
            Trace = trace ?? NullTraceRecorder.Instance,
            View = view ?? SilentProgressView.Discard,
        });
    }
}

internal static class FakeExtensions
{
    public static ImmutableArray<string> Paths(this IEnumerable<VirtualFile> files) => [.. files.Select(x => x.Path.FullName)];
}
