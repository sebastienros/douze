using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: image-map</c>. Collects the <c>image</c> metadata of the outputs of <c>images</c> into <c>images</c>, by
/// source url (<c>images["/docs/a.png"]</c>), for <c>pictures</c> and templates. Reads metadata only and emits no file.
/// </summary>
internal sealed class ImageMapStep : Step, IReduceStep
{
    public const string Key = "images";

    public ImageMapStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override bool ReadsContent => false;

    public ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        // The outputs of one source share their image node: the first of each source (files are sorted) is kept.
        var bySource = new SortedDictionary<string, Node>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (file.Metadata.TryGetOwn("image", out var node) && node is NodeObject image && image["source"] is NodeValue<string> source)
            {
                bySource.TryAdd(source.Value, image);
            }
        }

        var images = new NodeObject();

        foreach (var (source, image) in bySource)
        {
            images[source] = image;
        }

        return new(new StepOutput(FileList.None, new SiteScope($"step:{Name}", new NodeObject { [Key] = images })));
    }
}
