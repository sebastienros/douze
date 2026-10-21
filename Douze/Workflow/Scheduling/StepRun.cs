using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// The run of one step: its state, counters, errors, contribution and the results of its units.
/// Updated by the scheduler while the build runs; read-only for everything else.
/// </summary>
internal sealed class StepRun
{
    private readonly ConcurrentQueue<UnitResult> _results = new();
    private readonly ConcurrentQueue<BuildError> _errors = new();
    private IReadOnlyList<UnitResult>? _sortedResults;
    private IReadOnlyList<BuildError>? _sortedErrors;
    private int _filesIn;
    private int _filesConsumed;
    private int _filesOut;
    private int _filesPassedThrough;
    private int _units;
    private int _unitsDone;

    public StepRun(GraphNode node)
    {
        Node = node;
    }

    public GraphNode Node { get; }

    public StepState State { get; internal set; } = StepState.Pending;

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>Files that entered the step (after <c>inputs</c> filtering).</summary>
    public int FilesIn => Volatile.Read(ref _filesIn);

    /// <summary>Files that matched and were processed.</summary>
    public int FilesConsumed => Volatile.Read(ref _filesConsumed);

    /// <summary>Files the step produced.</summary>
    public int FilesOut => Volatile.Read(ref _filesOut);

    /// <summary>Files that did not match and passed through unchanged.</summary>
    public int FilesPassedThrough => Volatile.Read(ref _filesPassedThrough);

    /// <summary>Map: files consumed; group: groups; reduce: 1.</summary>
    public int Units => Volatile.Read(ref _units);

    public int UnitsDone => Volatile.Read(ref _unitsDone);

    /// <summary>
    /// Whether <see cref="Units"/> is final. A streaming map step knows its total only when its input is complete.
    /// </summary>
    public bool UnitsFinal { get; internal set; }

    /// <summary>
    /// Errors sorted by file then message, so that the order does not depend on scheduling.
    /// </summary>
    public IReadOnlyList<BuildError> Errors => _sortedErrors ?? [.. _errors];

    /// <summary>
    /// The scopes the step saw: the merged contributions of its ancestors.
    /// </summary>
    public ScopeSet Scopes { get; internal set; } = ScopeSet.Empty;

    /// <summary>
    /// What this step contributed (empty for map and group steps).
    /// </summary>
    public ScopeSet Contributions { get; internal set; } = ScopeSet.Empty;

    /// <summary>
    /// One result per unit, sorted by unit key once the step is done: refs and dependencies, not contents.
    /// </summary>
    public IReadOnlyList<UnitResult> Results => _sortedResults ?? [.. _results];

    internal void AddFileIn() => Interlocked.Increment(ref _filesIn);
    internal void AddConsumed() => Interlocked.Increment(ref _filesConsumed);
    internal void AddOut(int count) => Interlocked.Add(ref _filesOut, count);
    internal void AddPassedThrough() => Interlocked.Increment(ref _filesPassedThrough);
    internal void AddUnits(int count) => Interlocked.Add(ref _units, count);
    internal void AddUnitDone() => Interlocked.Increment(ref _unitsDone);
    internal void AddResult(UnitResult result) => _results.Enqueue(result);
    internal void AddError(BuildError error) => _errors.Enqueue(error);

    internal void Seal()
    {
        _sortedResults = [.. _results.OrderBy(x => x.Key.Unit, StringComparer.Ordinal)];
        _sortedErrors = [.. _errors
            .OrderBy(x => x.File?.FullName ?? "", StringComparer.Ordinal)
            .ThenBy(x => x.Message, StringComparer.Ordinal)];
    }

    public override string ToString() => $"{Node.Definition.Name}: {State}";
}

/// <summary>
/// The result of a unit of work: its key, the refs of its inputs and outputs and what it read besides its inputs. The
/// output files themselves, with their contents, are kept in <see cref="Files"/> only with
/// <see cref="BuildOptions.KeepFiles"/>: a build does not hold the contents of every step in memory.
/// </summary>
internal sealed record UnitResult(UnitKey Key, ImmutableArray<FileRef> Inputs, ImmutableArray<FileRef> Outputs, DependencySet Dependencies)
{
    public ImmutableArray<VirtualFile> Files { get; init; } = [];
}
