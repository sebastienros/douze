using System.Collections.Frozen;

namespace Douze.Workflow;

/// <summary>
/// The built-in steps, by <c>uses</c> id. Phase 1b registers data, frontmatter, parse, markdown, paginate,
/// permalink, collect, liquid, layout and write.
/// </summary>
internal static class BuiltInSteps
{
    public static IReadOnlyDictionary<string, Func<StepDefinition, Step>> Registry { get; } =
        new Dictionary<string, Func<StepDefinition, Step>>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
}
