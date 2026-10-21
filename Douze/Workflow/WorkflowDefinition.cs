using System.Collections.Immutable;

namespace Douze.Workflow;

// SourceFile: The workflow file, or <c>null</c> for the default workflow.
internal sealed record WorkflowDefinition(ImmutableArray<StepDefinition> Steps, string? SourceFile);
