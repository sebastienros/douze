using Douze.FileSystems;
using Douze.Nodes;
using Douze.Tracing;
using Douze.Workflow.Scheduling;

namespace Douze.Workflow;

internal sealed class BuildResult
{
    public BuildResult(IReadOnlyList<StepRun> steps, IReadOnlyList<BuildError> errors, IReadOnlyList<FileRef> outputs, ScopeSet scopes, TraceSession trace, TimeSpan elapsed, bool cancelled)
    {
        Steps = steps;
        Errors = errors;
        Outputs = outputs;
        Scopes = scopes;
        Trace = trace;
        Elapsed = elapsed;
        Cancelled = cancelled;
    }

    /// <summary>
    /// No errors and not cancelled.
    /// </summary>
    public bool Succeeded => Errors.Count == 0 && !Cancelled;

    public bool Cancelled { get; }

    public IReadOnlyList<BuildError> Errors { get; }

    /// <summary>
    /// The runs of every step, in topological order.
    /// </summary>
    public IReadOnlyList<StepRun> Steps { get; }

    /// <summary>
    /// The refs of the files that reached <c>write</c> steps, sorted by path.
    /// </summary>
    public IReadOnlyList<FileRef> Outputs { get; }

    /// <summary>
    /// The merged contributions of every step.
    /// </summary>
    public ScopeSet Scopes { get; }

    public TraceSession Trace { get; }

    /// <summary>
    /// The files the units of the build wrote to the output folder (now, or in the build whose results they replayed),
    /// with their length and modification time once written, sorted by path.
    /// </summary>
    public IReadOnlyList<(FPath Path, long Length, DateTime LastWriteTimeUtc)> Written { get; init; } = [];

    /// <summary>
    /// The files of the output folder that a previous build wrote and this one did not, deleted, sorted.
    /// </summary>
    public IReadOnlyList<FPath> Deleted { get; init; } = [];

    /// <summary>
    /// The snapshot of the source folder the build used: a watch session updates it for the next build.
    /// </summary>
    public SourceSnapshot? Sources { get; init; }

    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The units that ran (or failed), in every step.
    /// </summary>
    public int UnitsExecuted => Steps.Sum(x => x.UnitsExecuted);

    /// <summary>
    /// The units whose results came from the cache, in every step.
    /// </summary>
    public int UnitsReplayed => Steps.Sum(x => x.UnitsReplayed);

    public StepRun this[string step] => Steps.First(x => x.Node.Definition.Name == step);

    /// <summary>
    /// The metadata of the items of <c>collections.&lt;name&gt;</c>, from step contributions. Empty when absent.
    /// </summary>
    public IReadOnlyList<Metadata> Collection(string name)
    {
        foreach (var scope in Scopes.Site)
        {
            if (!scope.Source.StartsWith("step:", StringComparison.Ordinal)
                || scope.Values["collections"] is not NodeObject collections
                || collections[name] is not NodeArray items)
            {
                continue;
            }

            return items
                .Select(x => x is NodeObject item && item["data"] is NodeValue<Metadata> data ? data.Value : null)
                .OfType<Metadata>()
                .ToArray();
        }

        return [];
    }

    /// <summary>
    /// Resolves <paramref name="key"/> for <paramref name="metadata"/> through the final scopes, without tracking.
    /// </summary>
    public Node? Resolve(Metadata metadata, string key)
    {
        return Scopes.TryResolve(metadata, key, null, null, out var value, out _) ? value : null;
    }
}
