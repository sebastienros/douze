using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Tests.Workflow.Steps;

/// <summary>
/// Runs built-in steps in small workflows over a memory file system.
/// </summary>
public abstract class StepTestBase : SiteTestBase
{
    internal static StepDefinition Step(string name, string uses, string[]? needs = null, string[]? inputs = null, NodeObject? with = null, string? groupBy = null, string[]? match = null)
    {
        return new StepDefinition
        {
            Name = name,
            Uses = uses,
            Needs = [.. needs ?? []],
            Inputs = [.. inputs ?? []],
            Match = [.. match ?? []],
            With = with ?? new NodeObject(),
            GroupBy = groupBy,
        };
    }

    internal static WorkflowDefinition Workflow(params StepDefinition[] steps) => new([.. steps], null);

    internal static NodeObject With(params (string Key, Node? Value)[] options)
    {
        var with = new NodeObject();

        foreach (var (key, value) in options)
        {
            with[key] = value;
        }

        return with;
    }

    /// <summary>
    /// The files a step emitted, sorted by path.
    /// </summary>
    internal static VirtualFile[] Outputs(StepRun run) => [.. run.Results.SelectMany(x => x.Files).OrderBy(x => x.Path)];

    internal static Task<string> TextAsync(VirtualFile file) => file.Content.ReadTextAsync(Xunit.TestContext.Current.CancellationToken).AsTask();
}
