using Douze.Liquid;
using Douze.Nodes;
using Fluid;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: replace</c>. Replaces literal tokens in the content of each file (<c>with.tokens</c>: token to value), code
/// fences included. Each value is a Liquid template rendered with the file's model, once per file and only for the
/// tokens the file contains, so that a file depends on the data its tokens read and on nothing else. Tokens are found
/// with an ordinal search, in the order of <c>with.tokens</c>. A file that contains no token, or that
/// <c>with.skip</c> matches, passes through as it is.
/// </summary>
internal sealed class ReplaceStep : Step, IMapStep
{
    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.md", "**/*.html"]);

    private readonly (string Token, string Template)[] _tokens;
    private readonly GlobSet? _skip;

    public ReplaceStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "tokens", "skip");
        _skip = options.GetGlobs("skip");

        if (definition.With["tokens"] is not NodeObject tokens || tokens.Count == 0)
        {
            throw new WorkflowValidationException($"step '{Name}' needs the option 'tokens', a mapping of tokens to values", definition.Location);
        }

        var list = new List<(string, string)>(tokens.Count);

        foreach (var (token, value) in tokens)
        {
            if (token.Length == 0)
            {
                throw new WorkflowValidationException($"option 'tokens' of step '{Name}' has an empty token", definition.Location);
            }

            var text = value switch
            {
                null => "",
                NodeValue<string> s => s.Value,
                NodeValue and IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                NodeValue v => v.ToString() ?? "",
                _ => throw new WorkflowValidationException($"the value of token '{token}' of step '{Name}' must be a string", definition.Location),
            };

            list.Add((token, text));
        }

        _tokens = [.. list];
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Pages;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!file.Content.IsText || _skip?.IsMatch(file.Path) == true)
        {
            return FileList.Of(file);
        }

        var text = await file.Content.ReadTextAsync(ct);
        string? result = null;

        foreach (var (token, value) in _tokens)
        {
            var current = result ?? text;

            if (!current.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }

            var template = context.Build.Templates.GetOrParse(Hash128.OfText(value), value, $"token '{token}' of step '{Name}'");
            var templateContext = context.CreateTemplateContext(new MetadataValue(file.Metadata), file, ct);
            var replacement = await template.Template.RenderAsync(templateContext, NullEncoder.Default);

            result = current.Replace(token, replacement, StringComparison.Ordinal);
        }

        return result == null ? FileList.Of(file) : FileList.Of(file.With(Name, content: FileContent.FromText(result)));
    }
}
