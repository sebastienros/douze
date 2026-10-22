using Douze.Progress;
using Douze.Tracing;
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

            BuildResult result;

            try
            {
                result = await SiteBuilder.BuildAsync(graph, site, options);
            }
            catch (WorkflowValidationException e)
            {
                // Invalid shortcodes: the build did not start.
                foreach (var error in e.Errors)
                {
                    Console.Error.WriteLine(error);
                }

                return ExitCodes.Usage;
            }

            // A failed or cancelled build is traced too: that is when the trace is the most useful.
            KeyValuePair<string, string>[] otherData = [new("douze", StepDefinition.DouzeVersion), new("site", sourceDirectory)];
            var traced = WriteTraceFile(view, "trace", commandLine.TracePath, s => ChromeTraceWriter.Write(result.Trace, s, otherData))
                & WriteTraceFile(view, "timeline", commandLine.TimelinePath, s => TimelineHtmlWriter.Write(result.Trace, s, otherData));

            return result.Cancelled ? ExitCodes.Cancelled : result.Succeeded && traced ? ExitCodes.Success : ExitCodes.BuildErrors;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    /// <summary>
    /// Writes <paramref name="path"/> when it is set and reports it. Returns <c>false</c> when the file cannot be written.
    /// </summary>
    private static bool WriteTraceFile(IProgressView view, string kind, string? path, Action<Stream> write)
    {
        if (path == null)
        {
            return true;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            using (var stream = File.Create(fullPath))
            {
                write(stream);
            }

            view.Message(LogLevel.Info, null, $"{kind} written to {path}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            view.Message(LogLevel.Error, null, $"cannot write the {kind} file '{path}': {e.Message}");
            return false;
        }
    }
}
