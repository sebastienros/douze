using System.Collections.Frozen;
using Douze.Workflow.Steps;

namespace Douze.Workflow;

/// <summary>
/// The built-in steps, by <c>uses</c> id.
/// </summary>
internal static class BuiltInSteps
{
    public static IReadOnlyDictionary<string, Func<StepDefinition, Step>> Registry { get; } =
        new Dictionary<string, Func<StepDefinition, Step>>(StringComparer.Ordinal)
        {
            ["data"] = static d => new DataStep(d),
            ["frontmatter"] = static d => new FrontMatterStep(d),
            ["parse"] = static d => new ParseStep(d),
            ["markdown"] = static d => new MarkdownStep(d),
            ["paginate"] = static d => new PaginateStep(d),
            ["permalink"] = static d => new PermalinkStep(d),
            ["collect"] = static d => new CollectStep(d),
            ["liquid"] = static d => new LiquidStep(d),
            ["layout"] = static d => new LayoutStep(d),
            ["write"] = static d => new WriteStep(d),
            ["replace"] = static d => new ReplaceStep(d),
            ["fork"] = static d => new ForkStep(d),
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
