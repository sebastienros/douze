using Douze.Progress;
using Douze.Tools;
using Douze.Workflow;

namespace Douze.Cli;

/// <summary>
/// <c>douze tools install</c>: installs every tool the steps of the workflow download (<see cref="NpmToolRequirement"/>)
/// into the tools folder of the cache, so that a CI cache or an offline build has them. Exits with 1 when one cannot be
/// installed.
/// </summary>
internal static class ToolsCommand
{
    public static async Task<int> InstallAsync(CommandLine commandLine, TextWriter output, TextWriter error)
    {
        if (await BuildCommand.LoadAsync(commandLine) is not { } loaded)
        {
            return ExitCodes.Usage;
        }

        var (_, graph, site) = loaded;

        using var cancellation = new CancellationTokenSource();
        using var _ = BuildCommand.HandleCancelKey(cancellation);

        // The downloads are reported through the view.
        var view = commandLine.Quiet ? SilentProgressView.Discard : (IProgressView)new PlainProgressView(output, commandLine.Verbose);
        await using var context = await DoctorCommand.CreateContextAsync(commandLine, site, view, cancellation.Token);

        if (context == null)
        {
            return ExitCodes.Usage;
        }

        var folders = new HashSet<string>(StringComparer.Ordinal);
        var failed = false;

        try
        {
            foreach (var node in graph.Nodes)
            {
                foreach (var tool in node.Step.Requirements(context).OfType<NpmToolRequirement>())
                {
                    if (!folders.Add(tool.Folder))
                    {
                        continue;
                    }

                    var status = await tool.CheckAsync(cancellation.Token);

                    if (status.State == RequirementState.Missing)
                    {
                        error.WriteLine($"error: {node.Definition.Name}: {tool.Message(status)}");
                        failed = true;
                        continue;
                    }

                    try
                    {
                        var path = status.Path ?? await tool.InstallAsync(cancellation.Token);
                        output.WriteLine($"{tool.Name}: {(status.State == RequirementState.Found ? "already installed" : "installed")} at {path}");
                    }
                    catch (DouzeException e)
                    {
                        error.WriteLine($"error: {node.Definition.Name}: cannot install {tool.Name}: {e.Message}");
                        failed = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        if (folders.Count == 0)
        {
            output.WriteLine("No step of the workflow downloads tools.");
        }

        return failed ? ExitCodes.BuildErrors : ExitCodes.Success;
    }
}
