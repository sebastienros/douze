using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: permalink</c>. Moves each file to its output path (see <see cref="PermalinkResolver"/>), without
/// <c>with.root</c> (default: the content folder), and sets <c>url</c> to it. A <c>permalink: false</c> file is dropped.
/// </summary>
internal sealed class PermalinkStep : Step, IMapStep
{
    private readonly FPath? _root;

    public PermalinkStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "root");
        _root = options.GetString("root") is { } root ? StepPaths.Directory(root) : (FPath?)null;
    }

    public override StepShape NativeShape => StepShape.Map;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var path = await PermalinkResolver.ResolveAsync(file, file.Metadata, context, _root ?? context.Build.Site.ContentFolder, ct);

        if (path is not { } output)
        {
            return FileList.None;
        }

        var metadata = file.Metadata.With(own => own["url"] = NodeValue.Create(output.FullName));

        return FileList.Of(file.With(Name, path: output, metadata: metadata));
    }
}
