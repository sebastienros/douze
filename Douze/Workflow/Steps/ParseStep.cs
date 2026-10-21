using System.Text.Json.Nodes;
using System.Xml.Linq;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: parse</c>. Parses the content of a data file (YAML, JSON, XML) into its metadata under <c>with.as</c>
/// (default <c>data</c>), and keeps the content.
/// </summary>
internal sealed class ParseStep : Step, IMapStep
{
    private static readonly GlobSet DataFiles = GlobSet.Parse(["**/*.{json,yml,yaml,xml}"]);

    private readonly string _as;

    public ParseStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "as");
        _as = options.GetString("as", "data")!;
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => DataFiles;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var value = await ParseAsync(file, ct);
        var metadata = file.Metadata.With(own => own[_as] = value);

        return FileList.Of(file.With(Name, metadata: metadata));
    }

    internal static async ValueTask<Node?> ParseAsync(VirtualFile file, CancellationToken ct)
    {
        await using var stream = await file.Content.OpenReadAsync(ct);

        switch (file.Path.GetExtensionWithDot().ToLowerInvariant())
        {
            case ".yml":
            case ".yaml":
                using (var reader = new StreamReader(stream))
                {
                    return YamlHelper.Parse(reader);
                }

            case ".json":
                return JsonHelper.Convert(await JsonNode.ParseAsync(stream, cancellationToken: ct));

            case ".xml":
                return XmlHelper.Convert(await XDocument.LoadAsync(stream, LoadOptions.None, ct));

            default:
                throw new DouzeException($"unsupported data file '{file.Path}': use .yml, .yaml, .json or .xml");
        }
    }
}
