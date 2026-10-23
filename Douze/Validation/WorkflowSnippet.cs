using Douze.Workflow;
using Douze.Workflow.Yaml;
using SharpYaml.Model;

namespace Douze.Validation;

/// <summary>
/// Checks a <c>douze.yml</c> sample against the workflow schema: what <see cref="WorkflowLoader"/> rejects (unknown keys,
/// invalid values) and what the steps reject when they are created (unknown <c>uses</c>, unknown or invalid
/// <c>with</c> options, unknown <c>tool</c>). The graph is not validated: a sample names steps it does not show
/// (<c>needs: render</c>). A sample of the <c>steps</c> mapping alone (every top-level value is a mapping with
/// <c>uses</c>, <c>run</c>, <c>needs</c> or <c>inputs</c>) is checked as the content of <c>steps:</c>.
/// </summary>
internal static class WorkflowSnippet
{
    private static readonly string[] StepMarkers = ["uses", "run", "needs", "inputs"];

    // What 'enabled' expressions see: the same on every machine, so that the result does not depend on it.
    private static readonly WorkflowEnvironment Environment = new("other", "other", new Dictionary<string, string>(), watch: false);

    /// <param name="code">The sample, valid YAML.</param>
    /// <param name="stream">The sample, parsed.</param>
    public static async ValueTask<IReadOnlyList<CodeError>> CheckAsync(string code, YamlStream stream)
    {
        var steps = IsStepsExcerpt(stream);

        // An excerpt of 'steps' is indented under a 'steps:' line: positions move by a line and two columns.
        var text = steps ? "steps:\n" + string.Join('\n', code.Split('\n').Select(x => x.Length == 0 ? x : "  " + x)) : code;
        var (lineShift, columnShift) = steps ? (1, 2) : (0, 0);
        var errors = new List<CodeError>();

        CodeError ToError(string message, SourceLocation? location) => location is { } at
            ? new CodeError(Math.Max(1, at.Line - lineShift), Math.Max(1, at.Column - columnShift), message)
            : new CodeError(0, 0, message);

        WorkflowFile file;

        try
        {
            file = WorkflowLoader.Load(text, "douze.yml", environment: Environment);
        }
        catch (WorkflowValidationException e)
        {
            return [.. e.Errors.Select(x => ToError(x.Message, x.Location))];
        }
        catch (DouzeException e)
        {
            return [new CodeError(0, 0, e.Message)];
        }

        foreach (var definition in file.Workflow?.Steps ?? [])
        {
            try
            {
                var step = StepFactory.Create(definition);
                await step.DisposeAsync();
            }
            catch (WorkflowValidationException e)
            {
                errors.AddRange(e.Errors.Select(x => ToError(x.Message, x.Location)));
            }
            catch (DouzeException e)
            {
                errors.Add(ToError(e.Message, definition.Location));
            }
        }

        return errors;
    }

    private static bool IsStepsExcerpt(YamlStream stream)
    {
        if (stream.Count != 1 || stream[0].Contents is not YamlMapping { Count: > 0 } root)
        {
            return false;
        }

        foreach (var (key, value) in root)
        {
            if (key is not YamlValue { Value: var name } || WorkflowSchema.TopLevelKeys.Contains(name))
            {
                return false;
            }

            if (value is not YamlMapping step || !step.Any(x => x.Key is YamlValue { Value: var field } && StepMarkers.Contains(field)))
            {
                return false;
            }
        }

        return true;
    }
}
