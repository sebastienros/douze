using Douze.FileSystems;
using Douze.Workflow;
using Douze.Workflow.Yaml;

namespace Douze.Cli;

/// <summary>
/// Finds the site options and the workflow of a build: the workflow file given with <c>--workflow</c>, or
/// <c>douze.yml</c> at the source root when it exists; its <c>site</c> section, then detection, for the folders;
/// its <c>markdown</c> section; and its <c>steps</c>, or the default workflow.
/// </summary>
internal static class WorkflowSource
{
    public const string DefaultFileName = "douze.yml";

    public const string DefaultOutput = "_site";

    /// <param name="commandLine">The parsed command line.</param>
    /// <param name="sourceDirectory">The absolute path of the source folder.</param>
    /// <exception cref="WorkflowValidationException">The workflow file is missing or invalid.</exception>
    public static async Task<(WorkflowDefinition Workflow, SiteOptions Site)> LoadAsync(CommandLine commandLine, string sourceDirectory)
    {
        var file = await LoadFileAsync(commandLine, sourceDirectory);

        // -o is relative to the current folder, site.output to the source folder.
        var outputDirectory = commandLine.Output != null ? Path.GetFullPath(commandLine.Output)
            : file?.Site.Output != null ? Path.GetFullPath(file.Site.Output, sourceDirectory)
            : Path.GetFullPath(DefaultOutput);

        var site = await SiteOptions.DetectAsync(
            new PhysicalFileSystem(sourceDirectory),
            new PhysicalFileSystem(outputDirectory),
            sourceDirectory,
            outputDirectory,
            file?.Site,
            file?.Markdown);

        return (file?.Workflow ?? DefaultWorkflow.Create(site), site);
    }

    private static async Task<WorkflowFile?> LoadFileAsync(CommandLine commandLine, string sourceDirectory)
    {
        string path;

        if (commandLine.Workflow != null)
        {
            path = Path.GetFullPath(commandLine.Workflow);

            if (!File.Exists(path))
            {
                throw new WorkflowValidationException($"the workflow file '{commandLine.Workflow}' does not exist");
            }
        }
        else
        {
            path = Path.Combine(sourceDirectory, DefaultFileName);

            if (!File.Exists(path))
            {
                return null;
            }
        }

        // Locations are relative to the current folder, like the paths of the command line, unless the file is outside it.
        var fileName = Path.GetRelativePath(Environment.CurrentDirectory, path);

        var environment = WorkflowEnvironment.Current(watch: commandLine.Command is Command.Watch or Command.Serve);
        return await WorkflowLoader.LoadAsync(path, fileName.StartsWith("..", StringComparison.Ordinal) ? path : fileName, environment);
    }
}
