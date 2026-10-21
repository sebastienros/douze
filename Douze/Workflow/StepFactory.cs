namespace Douze.Workflow;

internal static class StepFactory
{
    /// <summary>
    /// Instantiates the step of a definition: a command step when <see cref="StepDefinition.Run"/> is set, a built-in step otherwise.
    /// </summary>
    /// <exception cref="WorkflowValidationException">Unknown <c>uses</c>, invalid <c>with</c> options, or <c>run</c> and <c>uses</c> together.</exception>
    public static Step Create(StepDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Run is not null && definition.Uses is not null)
        {
            throw new WorkflowValidationException($"step '{definition.Name}' cannot have both 'uses' and 'run'", definition.Location);
        }

        if (definition.Run is not null)
        {
            return CommandStepFactory.Create(definition);
        }

        if (definition.Uses is null)
        {
            throw new WorkflowValidationException($"step '{definition.Name}' needs 'uses' or 'run'", definition.Location);
        }

        if (!BuiltInSteps.Registry.TryGetValue(definition.Uses, out var factory))
        {
            throw new WorkflowValidationException(
                $"unknown built-in step '{definition.Uses}'{WorkflowValidationException.Suggest(definition.Uses, BuiltInSteps.Registry.Keys)}",
                definition.Location);
        }

        return factory(definition);
    }
}
