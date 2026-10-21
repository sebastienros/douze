namespace Douze.Workflow;

/// <summary>
/// Creates the steps that run commands (<c>run:</c>). Kept apart from <see cref="StepFactory"/> so that command steps
/// can be implemented independently.
/// </summary>
internal static class CommandStepFactory
{
    public static Step Create(StepDefinition definition)
    {
        throw new WorkflowValidationException("command steps are not available yet", definition.Location);
    }
}
