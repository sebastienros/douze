using System.Text.Encodings.Web;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// Computes the group key of a file for <c>group-by</c>: <c>file</c> is the file path, <c>dir</c> its directory, and
/// anything else a Liquid template rendered with <c>file</c> (<c>path</c>, <c>dir</c>, <c>name</c>, <c>ext</c>) and
/// <c>data</c> (the file's metadata) in scope.
/// </summary>
internal sealed class GroupKeyResolver
{
    private readonly string _groupBy;
    private readonly ParsedTemplate? _template;
    private readonly TemplateOptions _options;

    public GroupKeyResolver(string groupBy, BuildContext context, string step)
    {
        _groupBy = groupBy;
        _options = context.LiquidOptions;

        if (groupBy is not ("file" or "dir"))
        {
            _template = context.Templates.GetOrParse(Hash128.OfText(groupBy), groupBy, $"group-by of step '{step}'");
        }
    }

    public async ValueTask<string> GetKeyAsync(VirtualFile file, ScopeSet scopes, DependencyTracker deps, CancellationToken ct)
    {
        switch (_groupBy)
        {
            case "file":
                return file.Path.FullName;
            case "dir":
                return file.Path.GetDirectory().FullName;
        }

        var context = new TemplateContext(_options) { CancellationToken = ct };
        context.AmbientValues[AmbientKeys.Scopes] = scopes;
        context.AmbientValues[AmbientKeys.Dependencies] = deps;
        context.AmbientValues[AmbientKeys.File] = file;
        context.SetValue("file", new NodeObjectValue(new NodeObject
        {
            ["path"] = NodeValue.Create(file.Path.FullName),
            ["dir"] = NodeValue.Create(file.Path.GetDirectory().FullName),
            ["name"] = NodeValue.Create(Path.GetFileName(file.Path.FullName)),
            ["ext"] = NodeValue.Create(file.Path.GetExtensionWithDot()),
        }));
        context.SetValue("data", new MetadataValue(file.Metadata));

        var key = await _template!.Template.RenderAsync(context, NullEncoder.Default);
        return key.Trim();
    }
}
