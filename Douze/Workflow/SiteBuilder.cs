using System.Diagnostics;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow.Scheduling;

namespace Douze.Workflow;

/// <summary>
/// Builds a site with a workflow.
/// </summary>
internal static class SiteBuilder
{
    /// <exception cref="WorkflowValidationException">The workflow is invalid.</exception>
    public static Task<BuildResult> BuildAsync(WorkflowDefinition workflow, SiteOptions site, BuildOptions options)
    {
        return BuildAsync(WorkflowGraph.Build(workflow), site, options);
    }

    public static async Task<BuildResult> BuildAsync(WorkflowGraph graph, SiteOptions site, BuildOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var view = options.View ?? SilentProgressView.Discard;
        var trace = options.Trace ?? new TraceRecorder(options.TraceUnits);

        await using var context = new BuildContext(site, options, view, trace);

        foreach (var warning in graph.Warnings)
        {
            view.Message(LogLevel.Warning, null, warning);
        }

        view.BuildStarted(graph);

        SchedulerResult result;

        using (trace.Begin("build", "build"))
        {
            result = await new Scheduler(graph, context).RunAsync(options.Cancellation);
        }

        var outputs = result.Runs
            .Where(x => x.Node.Definition.Uses == "write")
            .SelectMany(x => x.Results)
            .SelectMany(x => x.Outputs)
            .OrderBy(x => x.Path)
            .ToArray();

        var buildResult = new BuildResult(result.Runs, result.Errors, outputs, result.FinalScopes, trace.Session, stopwatch.Elapsed, result.Cancelled);

        view.BuildCompleted(buildResult);

        return buildResult;
    }
}
