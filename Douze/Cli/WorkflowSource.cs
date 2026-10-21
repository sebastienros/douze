using Douze.FileSystems;
using Douze.Workflow;

namespace Douze.Cli;

/// <summary>
/// Finds the site options and the workflow of a build: the folders detected in the source folder, then the workflow
/// of the file given with <c>--workflow</c>, of <c>douze.yml</c> at the source root, or the default workflow.
/// </summary>
internal static class WorkflowSource
{
    public const string DefaultFileName = "douze.yml";

    public const string DefaultOutput = "_site";

    /// <param name="commandLine">The parsed command line.</param>
    /// <param name="sourceDirectory">The absolute path of the source folder.</param>
    /// <exception cref="WorkflowValidationException">The workflow file is invalid.</exception>
    public static async Task<(WorkflowDefinition Workflow, SiteOptions Site)> LoadAsync(CommandLine commandLine, string sourceDirectory)
    {
        // TODO(phase 2): the 'site' section of the workflow file sets the folders, the output (when -o is not given)
        // and the parallelism (when -p is not given).
        var outputDirectory = Path.GetFullPath(commandLine.Output ?? DefaultOutput);
        var site = await SiteOptions.DetectAsync(new PhysicalFileSystem(sourceDirectory), new PhysicalFileSystem(outputDirectory), sourceDirectory, outputDirectory);

        // TODO(phase 2): load workflow files.
        if (commandLine.Workflow != null || (await site.Source.GetFileEntryAsync("/" + DefaultFileName)).Exists)
        {
            throw new WorkflowValidationException("workflow files are not available yet");
        }

        return (DefaultWorkflow.Create(site), site);
    }
}
