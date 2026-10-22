using System.Runtime.InteropServices;
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
        if (await LoadAsync(commandLine) is not { } loaded)
        {
            return ExitCodes.Usage;
        }

        var (sourceDirectory, graph, site) = loaded;

        if (commandLine.PrintWorkflow)
        {
            Console.Write(graph.ToYaml());
            return ExitCodes.Success;
        }

        using var cancellation = new CancellationTokenSource();
        using var _ = HandleCancelKey(cancellation);

        using var view = ViewSelector.Select(commandLine);

        var result = await SiteBuilder.BuildAsync(graph, site, Options(commandLine, site, view, cancellation.Token));
        var traced = WriteTraceFiles(commandLine, view, result, sourceDirectory);

        return result.Cancelled ? ExitCodes.Cancelled : result.Succeeded && traced ? ExitCodes.Success : ExitCodes.BuildErrors;
    }

    /// <summary>
    /// The source folder, the graph and the site of the command line, or null when they are invalid (the errors are
    /// printed).
    /// </summary>
    internal static async Task<(string SourceDirectory, WorkflowGraph Graph, SiteOptions Site)?> LoadAsync(CommandLine commandLine)
    {
        var sourceDirectory = Path.GetFullPath(commandLine.Source);

        if (!Directory.Exists(sourceDirectory))
        {
            Console.Error.WriteLine($"error: the source folder '{sourceDirectory}' does not exist");
            return null;
        }

        try
        {
            var (workflow, site) = await WorkflowSource.LoadAsync(commandLine, sourceDirectory);
            return (sourceDirectory, WorkflowGraph.Build(workflow), site);
        }
        catch (WorkflowValidationException e)
        {
            foreach (var error in e.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return null;
        }
    }

    /// <summary>
    /// Cancels <paramref name="cancellation"/> on Ctrl+C or SIGTERM, until disposed. The first Ctrl+C stops the build
    /// cleanly; a second one ends the process.
    /// </summary>
    internal static IDisposable HandleCancelKey(CancellationTokenSource cancellation)
    {
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = !cancellation.IsCancellationRequested;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancel;

        var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        return new CancelKeyRegistration(cancel, terminate);
    }

    private sealed class CancelKeyRegistration(ConsoleCancelEventHandler cancel, PosixSignalRegistration? terminate) : IDisposable
    {
        public void Dispose()
        {
            Console.CancelKeyPress -= cancel;
            terminate?.Dispose();
        }
    }

    /// <summary>
    /// The build options of the command line.
    /// </summary>
    internal static BuildOptions Options(CommandLine commandLine, SiteOptions site, IProgressView? view, CancellationToken ct) => new()
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
        Cancellation = ct,
    };

    /// <summary>
    /// Writes the trace and the timeline of a build when the command line asks for them. Returns <c>false</c> when one
    /// cannot be written. A failed or cancelled build is traced too: that is when the trace is the most useful.
    /// </summary>
    internal static bool WriteTraceFiles(CommandLine commandLine, IProgressView view, BuildResult result, string sourceDirectory)
    {
        KeyValuePair<string, string>[] otherData = [new("douze", StepDefinition.DouzeVersion), new("site", sourceDirectory)];

        return WriteTraceFile(view, "trace", commandLine.TracePath, s => ChromeTraceWriter.Write(result.Trace, s, otherData))
            & WriteTraceFile(view, "timeline", commandLine.TimelinePath, s => TimelineHtmlWriter.Write(result.Trace, s, otherData));
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
