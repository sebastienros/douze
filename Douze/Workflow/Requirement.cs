namespace Douze.Workflow;

/// <summary>
/// Something a step needs outside Douze to run: a native library, a tool Douze downloads, an executable of a command.
/// Steps declare theirs (<see cref="Step.Requirements"/>); <c>douze doctor</c> checks them without building, and a build
/// checks them before any unit runs, failing with the <see cref="Hint"/> of each missing one.
/// </summary>
/// <param name="name">What is required, as users know it: <c>libvips</c>, <c>typescript 7.0.2</c>, <c>node</c>.</param>
/// <param name="hint">How to fix it when it is missing: what to install or configure.</param>
internal abstract class Requirement(string name, string hint)
{
    public string Name { get; } = name;

    public string Hint { get; } = hint;

    /// <summary>
    /// Checks the requirement without changing anything: no download, no process. An exception is reported as missing.
    /// </summary>
    public abstract ValueTask<RequirementStatus> CheckAsync(CancellationToken ct);

    /// <summary>
    /// The message of a requirement that is missing: <c>&lt;name&gt; &lt;detail&gt;: &lt;hint&gt;</c>.
    /// </summary>
    public string Message(RequirementStatus status) => $"{Name} {status.Detail ?? "is missing"}: {Hint}";

    /// <summary>
    /// A requirement checked by <paramref name="check"/>.
    /// </summary>
    public static Requirement Create(string name, string hint, Func<CancellationToken, ValueTask<RequirementStatus>> check) => new Checked(name, hint, check);

    private sealed class Checked(string name, string hint, Func<CancellationToken, ValueTask<RequirementStatus>> check) : Requirement(name, hint)
    {
        public override ValueTask<RequirementStatus> CheckAsync(CancellationToken ct) => check(ct);
    }
}

internal enum RequirementState
{
    /// <summary>Available: the build can use it.</summary>
    Found,

    /// <summary>Not available yet, but Douze downloads it when a build first needs it (<c>douze tools install</c> does it now).</summary>
    Installable,

    /// <summary>Not available and Douze cannot get it: a build fails before any unit runs.</summary>
    Missing,

    /// <summary>Cannot be checked before the build, such as a command whose executable is a template.</summary>
    Unchecked,
}

/// <param name="State">Whether the requirement is met.</param>
/// <param name="Version">The version found, when known.</param>
/// <param name="Path">Where it was found, when it is a file.</param>
/// <param name="Detail">Why it is not found or not checked, written to follow the name of the requirement
/// (<c>is not on PATH</c>, <c>cannot be loaded (...)</c>).</param>
internal sealed record RequirementStatus(RequirementState State, string? Version = null, string? Path = null, string? Detail = null)
{
    public static RequirementStatus Found(string? version = null, string? path = null) => new(RequirementState.Found, version, path);

    public static RequirementStatus Installable(string detail) => new(RequirementState.Installable, Detail: detail);

    public static RequirementStatus Missing(string detail) => new(RequirementState.Missing, Detail: detail);

    public static RequirementStatus Unchecked(string detail) => new(RequirementState.Unchecked, Detail: detail);
}

/// <summary>
/// The status of a requirement of a step.
/// </summary>
internal sealed record RequirementResult(string Step, Requirement Requirement, RequirementStatus Status)
{
    public bool IsMissing => Status.State == RequirementState.Missing;
}

internal static class Requirements
{
    /// <summary>
    /// Checks the requirements of every step of <paramref name="graph"/>, in topological order then in the order each
    /// step declares them.
    /// </summary>
    public static async Task<IReadOnlyList<RequirementResult>> CheckAsync(WorkflowGraph graph, BuildContext context, CancellationToken ct)
    {
        var results = new List<RequirementResult>();

        foreach (var node in graph.Nodes)
        {
            foreach (var requirement in node.Step.Requirements(context))
            {
                RequirementStatus status;

                try
                {
                    status = await requirement.CheckAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    status = RequirementStatus.Missing($"cannot be checked ({e.Message})");
                }

                results.Add(new RequirementResult(node.Definition.Name, requirement, status));
            }
        }

        return results;
    }
}
