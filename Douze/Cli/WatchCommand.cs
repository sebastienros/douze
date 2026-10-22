using Douze.Progress;
using Douze.Workflow;
using Douze.Workflow.Watch;

namespace Douze.Cli;

/// <summary>
/// <c>douze watch</c>: builds the site, then again after every change of its sources, until Ctrl+C.
/// <c>douze serve</c> also serves the output folder with live reload (<see cref="DevServer"/>).
/// </summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(CommandLine commandLine)
    {
        var serve = commandLine.Command == Command.Serve;

        // An invalid workflow is a usage error when the session starts; once it runs, it is reported and the session
        // waits for the next change.
        if (await BuildCommand.LoadAsync(commandLine) is not { } loaded)
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
        using var _ = BuildCommand.HandleCancelKey(cancellation);
        var token = cancellation.Token;

        using var sessionView = commandLine.Quiet ? (IProgressView)new SilentProgressView(Console.Error) : new PlainProgressView(Console.Out, commandLine.Verbose);
        var options = BuildCommand.Options(commandLine, site, null, token);
        using var builder = new IncrementalBuilder(() => WorkflowSource.LoadAsync(commandLine, sourceDirectory), options, sessionView);

        RebuildResult? result;

        using (var view = ViewSelector.Select(commandLine))
        {
            result = await BuildAsync(builder, null, view, token);

            if (result != null)
            {
                BuildCommand.WriteTraceFiles(commandLine, view, result.Build, sourceDirectory);
            }
        }

        if (token.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }

        LiveReload? reload = null;
        DevServer? server = null;

        try
        {
            if (serve)
            {
                reload = new LiveReload();
                server = new DevServer(() => builder.Site?.OutputDirectory, commandLine.Port, reload);

                try
                {
                    server.Start();
                }
                catch (DouzeException e)
                {
                    Console.Error.WriteLine($"error: {e.Message}");
                    return ExitCodes.Usage;
                }

                sessionView.Message(LogLevel.Info, null, $"Serving {Relative(builder.Site?.OutputDirectory ?? "")} at {server.Url}");
            }

            using var watcher = new FileWatcher(sourceDirectory, builder.IsIgnored, FileWatcher.DefaultDebounce);

            if (commandLine.Workflow is { } workflow && Path.GetFullPath(workflow) is var workflowPath
                && !workflowPath.StartsWith(Path.TrimEndingDirectorySeparator(sourceDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                watcher.WatchFile(workflowPath);
            }

            sessionView.Message(LogLevel.Info, null, $"Watching {Relative(sourceDirectory)} for changes (Ctrl+C to stop)");

            while (true)
            {
                ChangeSet changes;

                try
                {
                    changes = await watcher.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var view = RebuildView(commandLine, changes);
                var rebuilt = await BuildAsync(builder, changes, view, token);

                if (rebuilt == null)
                {
                    continue;
                }

                if (rebuilt.Build.Cancelled)
                {
                    break;
                }

                BuildCommand.WriteTraceFiles(commandLine, view, rebuilt.Build, sourceDirectory);
                reload?.Notify(rebuilt.Changed);
            }
        }
        finally
        {
            if (server != null)
            {
                await server.DisposeAsync();
            }
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Builds, and reports an exception the build did not turn into build errors (the source folder changing under its
    /// feet, a disk full): the session goes on, and the next change builds again. Returns null then.
    /// </summary>
    private static async Task<RebuildResult?> BuildAsync(IncrementalBuilder builder, ChangeSet? changes, IProgressView view, CancellationToken ct)
    {
        try
        {
            return await builder.BuildAsync(changes, view, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            view.Message(LogLevel.Error, null, $"the build stopped: {e.Message}");
            return null;
        }
    }

    private static IProgressView RebuildView(CommandLine commandLine, ChangeSet changes)
    {
        if (commandLine.Quiet)
        {
            return new SilentProgressView(Console.Error);
        }

        if (commandLine.Verbose)
        {
            return new PlainProgressView(Console.Out, verbose: true);
        }

        return new RebuildProgressView(Console.Out, changes.ToString);
    }

    private static string Relative(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }
}
