using Douze.Workflow;

namespace Douze.Cli;

/// <summary>
/// Finds the workflow of a site: the file given with <c>--workflow</c>, <c>douze.yml</c> at the source root, or the
/// default workflow.
/// </summary>
internal static class WorkflowSource
{
    public const string DefaultFileName = "douze.yml";

    /// <exception cref="WorkflowValidationException">The workflow file is invalid.</exception>
    public static async Task<WorkflowDefinition> LoadAsync(string? workflowFile, SiteOptions site)
    {
        // TODO(phase 2): load workflow files.
        if (workflowFile != null || (await site.Source.GetFileEntryAsync("/" + DefaultFileName)).Exists)
        {
            throw new WorkflowValidationException("workflow files are not available yet");
        }

        return DefaultWorkflow.Create(site);
    }
}
