using System.Globalization;
using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Progress;

/// <summary>
/// The texts the progress views share, so that the plain and the live views print the same lines.
/// </summary>
internal static class StepSummary
{
    /// <summary>
    /// The line of a completed step without its marker: <c>markdown        312 files    0.41 s</c>, followed by
    /// <c>   300 cached</c> when units were replayed from the cache, and <c>   140 shared</c> when units took the result of
    /// another variant of their file.
    /// </summary>
    public static string Line(StepRun run, int nameWidth)
    {
        var name = run.Node.Definition.Name.PadRight(nameWidth);
        var cached = run.UnitsReplayed > 0 ? string.Create(CultureInfo.InvariantCulture, $"  {run.UnitsReplayed,6} cached") : "";
        cached += run.UnitsShared > 0 ? string.Create(CultureInfo.InvariantCulture, $"  {run.UnitsShared,6} shared") : "";

        return run.State switch
        {
            StepState.Succeeded => string.Create(CultureInfo.InvariantCulture, $"{name}{Files(run.FilesConsumed)} {Duration(run).TotalSeconds,7:0.00} s{cached}"),
            StepState.Failed => $"{name}{FirstError(run)}",
            StepState.Cancelled => $"{name}cancelled",
            _ => $"{name}skipped",
        };
    }

    /// <summary>
    /// <c>Build succeeded: 12 steps, 13 files written in 0.41 s, 40 units executed, 12 cached</c>, or how the build
    /// failed.
    /// </summary>
    public static string Headline(BuildResult result)
    {
        var seconds = result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);

        if (result.Cancelled)
        {
            return $"Build cancelled after {seconds} s";
        }

        if (result.Errors.Count > 0)
        {
            return $"Build failed with {Plural(result.Errors.Count, "error")} in {seconds} s";
        }

        var cached = result.UnitsReplayed > 0 ? $", {result.UnitsReplayed} cached" : "";
        cached += result.UnitsShared > 0 ? $", {result.UnitsShared} shared" : "";

        return $"Build succeeded: {Plural(result.Steps.Count, "step")}, {Plural(result.Outputs.Count, "file")} written in {seconds} s, {Plural(result.UnitsExecuted, "unit")} executed{cached}";
    }

    /// <summary>
    /// The width of the step name column: the longest name plus two spaces, at least 12.
    /// </summary>
    public static int NameWidth(WorkflowGraph graph)
    {
        return Math.Max(12, graph.Nodes.Count == 0 ? 0 : graph.Nodes.Max(x => x.Definition.Name.Length) + 2);
    }

    public static TimeSpan Duration(StepRun run)
    {
        return run.StartedAt is { } start && run.EndedAt is { } end ? end - start : TimeSpan.Zero;
    }

    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // "     1 file " keeps the column of "   312 files" aligned.
    private static string Files(int count) => count == 1 ? "     1 file " : string.Create(CultureInfo.InvariantCulture, $"{count,6} files");

    private static string FirstError(StepRun run)
    {
        if (run.Errors.Count == 0)
        {
            return "failed";
        }

        var first = run.Errors[0].ToString();
        return run.Errors.Count == 1 ? first : $"{first} (and {Plural(run.Errors.Count - 1, "more error")})";
    }
}
