using Douze.Workflow.Commands;

namespace Douze.Workflow;

/// <summary>
/// Creates the steps that run commands (<c>run:</c>). Kept apart from <see cref="StepFactory"/> so that command steps
/// can be implemented independently.
/// </summary>
internal static class CommandStepFactory
{
    /// <exception cref="WorkflowValidationException">The definition is invalid for its mode.</exception>
    public static Step Create(StepDefinition definition)
    {
        return definition.Mode switch
        {
            CommandMode.Filter => new FilterCommandStep(definition),
            CommandMode.Batch => new BatchCommandStep(definition),
            _ => throw new WorkflowValidationException("worker command steps are not available yet", definition.Location),
        };
    }
}
