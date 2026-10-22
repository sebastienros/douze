using System.Diagnostics;
using Douze.FileSystems;
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

    /// <exception cref="WorkflowValidationException">A shortcode is invalid.</exception>
    public static async Task<BuildResult> BuildAsync(WorkflowGraph graph, SiteOptions site, BuildOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var view = options.View ?? SilentProgressView.Discard;
        var trace = options.Trace ?? new TraceRecorder(options.TraceUnits);

        await using var context = await BuildContext.CreateAsync(site, options, view, trace, options.Cancellation);

        foreach (var warning in graph.Warnings)
        {
            view.Message(LogLevel.Warning, null, warning);
        }

        view.BuildStarted(graph);

        // A step whose requirement is missing would fail its units: the build fails before any runs.
        IReadOnlyList<RequirementResult> requirements;

        using (trace.Begin("requirements", "io"))
        {
            requirements = await Requirements.CheckAsync(graph, context, options.Cancellation);
        }

        if (requirements.Any(x => x.IsMissing))
        {
            var errors = requirements.Where(x => x.IsMissing).Select(x => new BuildError(x.Step, null, x.Requirement.Message(x.Status), null, null)).ToArray();
            var failed = new BuildResult([.. graph.Nodes.Select(x => new StepRun(x) { State = StepState.Skipped })], errors, [], context.BaseScopes, trace.Session, stopwatch.Elapsed, cancelled: false);
            view.BuildCompleted(failed);
            return failed;
        }

        SchedulerResult result;

        using (trace.Begin("build", "build"))
        {
            result = await new Scheduler(graph, context).RunAsync(options.Cancellation);
        }

        var succeeded = result.Errors.Count == 0 && !result.Cancelled;
        var written = Written(result.Runs);
        IReadOnlyList<FPath> deleted = [];

        // What the previous builds wrote and this one did not is stale, unless the build failed: a page that failed may
        // be written again by the next build.
        if (succeeded && (options.PreviousWritten ?? context.Cache?.PreviousWritten()) is { } previous)
        {
            using (trace.Begin("stale outputs", "io"))
            {
                deleted = await StaleOutputs.DeleteAsync(context.Output, previous, written);
            }
        }

        if (context.Cache is { } cache)
        {
            using (trace.Begin("cache", "io"))
            {
                if (context.OwnsCache)
                {
                    cache.Save(succeeded);
                }
                else
                {
                    cache.Commit(succeeded);
                }
            }
        }

        var outputs = result.Runs
            .Where(x => x.Node.Definition.Uses == "write")
            .SelectMany(x => x.Results)
            .SelectMany(x => x.Outputs)
            .OrderBy(x => x.Path)
            .ToArray();

        var buildResult = new BuildResult(result.Runs, result.Errors, outputs, result.FinalScopes, trace.Session, stopwatch.Elapsed, result.Cancelled)
        {
            Written = written,
            Deleted = deleted,
            Sources = result.Sources,
        };

        view.BuildCompleted(buildResult);

        return buildResult;
    }

    /// <summary>
    /// The files the units of the build wrote to the output folder, ran or replayed, sorted by path.
    /// </summary>
    private static IReadOnlyList<(FPath Path, long Length, DateTime LastWriteTimeUtc)> Written(IReadOnlyList<StepRun> runs)
    {
        return [.. runs
            .SelectMany(x => x.Results)
            .SelectMany(x => x.Dependencies.Written)
            .OrderBy(x => x.Path)];
    }
}
