using System.Globalization;
using Douze.FileSystems;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;

namespace Douze.Workflow.Steps;

/// <summary>
/// Computes output paths, for <c>permalink</c> and <c>paginate</c>, so both produce identical paths.
/// </summary>
internal static class PermalinkResolver
{
    public const string Key = "permalink";

    /// <summary>
    /// Returns the output path of <paramref name="file"/> with <paramref name="metadata"/>, or <c>null</c> when its
    /// <c>permalink</c> is <c>false</c>. A <c>permalink</c> is a Liquid template rendered with the metadata; a result
    /// without an extension is a folder (<c>blog/post</c> is <c>/blog/post/index.html</c>). Without a <c>permalink</c>,
    /// an HTML file <c>name.html</c> becomes <c>name/index.html</c> (<c>index.html</c> stays in place) and other files
    /// keep their path. <paramref name="root"/> is removed from the start of the result.
    /// </summary>
    public static async ValueTask<FPath?> ResolveAsync(VirtualFile file, Metadata metadata, StepContext context, FPath root, CancellationToken ct)
    {
        if (!context.Scopes.TryResolve(metadata, Key, null, context.Dependencies, out var node, out _))
        {
            return Default(file.Path, root);
        }

        string? text;

        switch (node)
        {
            case NodeValue<bool> { Value: false }:
                return null;
            case NodeValue<string> s:
                text = s.Value;
                break;
            case NodeValue and IFormattable formattable:
                text = formattable.ToString(null, CultureInfo.InvariantCulture);
                break;
            default:
                text = null;
                break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Default(file.Path, root);
        }

        var template = context.Build.Templates.GetOrParse(Hash128.OfText(text), text, $"permalink of '{file.SourcePath ?? file.Path}'");
        var templateContext = context.CreateTemplateContext(new MetadataValue(metadata), file, ct);
        var rendered = await template.Template.RenderAsync(templateContext);

        var path = FPath.Combine(FPath.Root, rendered);

        // Without an extension, the permalink is a folder.
        if (string.IsNullOrEmpty(Path.GetExtension(path.FullName)))
        {
            path = FPath.Combine(FPath.Root, path, "index.html");
        }

        return StepPaths.StripRoot(path, root);
    }

    /// <summary>
    /// The output path of a file without <c>permalink</c>.
    /// </summary>
    public static FPath Default(FPath path, FPath root)
    {
        if (path.GetExtensionWithDot() == ".html")
        {
            // name.html -> name/index.html, so the URL is the folder; index.html stays in place.
            var name = Path.GetFileNameWithoutExtension(path.FullName);

            path = name != "index"
                ? FPath.Combine(path.GetDirectory(), name, "index.html")
                : FPath.Combine(path.GetDirectory(), "index.html");
        }

        return StepPaths.StripRoot(path, root);
    }

    /// <summary>
    /// The path of page <paramref name="page"/> of a paginated file whose pages all resolve to <paramref name="path"/>:
    /// <c>/a/index.html</c>, <c>/a/1/index.html</c>, <c>/a/2/index.html</c>...
    /// </summary>
    public static FPath Paged(FPath path, int page)
    {
        var name = Path.GetFileNameWithoutExtension(path.FullName);
        var directory = path.GetDirectory();

        if (name != "index")
        {
            directory = FPath.Combine(directory, name + "/");
        }

        return page == 0
            ? FPath.Combine(directory, "index.html")
            : FPath.Combine(directory, page.ToString(CultureInfo.InvariantCulture) + "/", "index.html");
    }
}
