using Douze.Progress;
using Douze.Workflow;

namespace Douze.Cli;

/// <summary>
/// <c>douze build</c>: builds the site once.
/// </summary>
internal static class BuildCommand
{
    public static async Task<int> RunAsync(CommandLine commandLine)
    {
        var sourceDirectory = Path.GetFullPath(commandLine.Source);

        if (!Directory.Exists(sourceDirectory))
        {
            Console.Error.WriteLine($"error: the source folder '{sourceDirectory}' does not exist");
            return ExitCodes.Usage;
        }

        WorkflowGraph graph;
        SiteOptions site;

        try
        {
            (var workflow, site) = await WorkflowSource.LoadAsync(commandLine, sourceDirectory);
            graph = WorkflowGraph.Build(workflow);
        }
        catch (WorkflowValidationException e)
        {
            foreach (var error in e.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return ExitCodes.Usage;
        }

        if (commandLine.PrintWorkflow)
        {
            Console.Write(graph.ToYaml());
            return ExitCodes.Success;
        }

        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            // The first Ctrl+C stops the build cleanly; a second one ends the process.
            e.Cancel = !cancellation.IsCancellationRequested;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;

        try
        {
            using var view = ViewSelector.Select(commandLine);

            // TODO(phase 4): write the trace and timeline files.
            if (commandLine.TracePath != null || commandLine.TimelinePath != null)
            {
                view.Message(LogLevel.Warning, null, "--trace and --timeline are not available yet");
            }

            var options = new BuildOptions
            {
                MaxDegreeOfParallelism = commandLine.Parallelism ?? site.Parallelism ?? Environment.ProcessorCount,
                Failure = commandLine.Failure,
                Verbose = commandLine.Verbose,
                TraceUnits = commandLine.TraceUnits,
                TracePath = commandLine.TracePath,
                TimelinePath = commandLine.TimelinePath,
                View = view,
                CacheDirectory = commandLine.CacheDirectory,
                UseCache = !commandLine.NoCache,
                Cancellation = cancellation.Token,
            };

            var result = await SiteBuilder.BuildAsync(graph, site, options);

            return result.Cancelled ? ExitCodes.Cancelled : result.Succeeded ? ExitCodes.Success : ExitCodes.BuildErrors;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
