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

    private const string Html = ".html";
    private const string Markdown = ".md";

    /// <summary>
    /// Returns the output path of <paramref name="file"/> with <paramref name="metadata"/>, or <c>null</c> when its
    /// <c>permalink</c> is <c>false</c>. A <c>permalink</c> is a Liquid template rendered with the metadata; a result
    /// without an extension is a folder (<c>blog/post</c> is <c>/blog/post/index.html</c>). Without a <c>permalink</c>,
    /// a page <c>name.html</c> becomes <c>name/index.html</c> (<c>index.html</c> stays in place) and other files keep
    /// their path. <paramref name="root"/> is removed from the start of the result, or the locale root
    /// (<see cref="LocaleSet.Root"/>) for a file under it, so that a page of <c>&lt;root&gt;/ja/</c> is under
    /// <c>/ja/</c>.
    /// </summary>
    /// <remarks>
    /// A Markdown file (<c>.md</c>) is a page too, whose content is not HTML yet: its HTML output path keeps the
    /// <c>.md</c> extension (<c>post.md</c> is <c>/post/index.md</c>), which the <c>markdown</c> step changes to
    /// <c>.html</c>, so steps between them still match it as Markdown. Its <see cref="Url"/> is the HTML path. A
    /// Markdown file whose <c>permalink</c> is not HTML (<c>feed.xml</c>) is not renamed, and not converted.
    /// </remarks>
    public static async ValueTask<FPath?> ResolveAsync(VirtualFile file, Metadata metadata, StepContext context, FPath root, CancellationToken ct)
    {
        root = EffectiveRoot(file.Path, root, context.Build.Locales);

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

        var isMarkdown = file.Path.GetExtensionWithDot() == Markdown;

        var template = context.Build.Templates.GetOrParse(Hash128.OfText(text), text, $"permalink of '{file.SourcePath ?? file.Path}'");
        var templateContext = context.CreateTemplateContext(new MetadataValue(metadata), file, ct);
        var rendered = await template.Template.RenderAsync(templateContext);

        var path = FPath.Combine(FPath.Root, rendered);

        // Without an extension, the permalink is a folder.
        if (string.IsNullOrEmpty(Path.GetExtension(path.FullName)))
        {
            path = FPath.Combine(FPath.Root, path, "index.html");
        }

        if (isMarkdown && path.GetExtensionWithDot() == Html)
        {
            path = new FPath(Path.ChangeExtension(path.FullName, Markdown));
        }

        return StepPaths.StripRoot(path, root);
    }

    /// <summary>
    /// The locale root when <paramref name="path"/> is under it and it is inside <paramref name="root"/>,
    /// <paramref name="root"/> otherwise.
    /// </summary>
    private static FPath EffectiveRoot(FPath path, FPath root, LocaleSet locales)
    {
        return locales.IsEnabled
            && locales.Root.FullName.StartsWith(root.FullName, StringComparison.Ordinal)
            && path.FullName.StartsWith(locales.Root.FullName, StringComparison.Ordinal)
            ? locales.Root
            : root;
    }

    /// <summary>
    /// The URL of an output path: the path, or for a Markdown page, the path of its HTML (<c>/post/index.md</c> is
    /// <c>/post/index.html</c>).
    /// </summary>
    public static string Url(FPath path)
    {
        return path.GetExtensionWithDot() == Markdown ? Path.ChangeExtension(path.FullName, Html) : path.FullName;
    }

    /// <summary>
    /// The output path of a file without <c>permalink</c>.
    /// </summary>
    public static FPath Default(FPath path, FPath root)
    {
        if (path.GetExtensionWithDot() is Html or Markdown)
        {
            // name.html -> name/index.html, so the URL is the folder; index.html stays in place. Markdown keeps .md.
            var name = Path.GetFileNameWithoutExtension(path.FullName);
            var index = "index" + path.GetExtensionWithDot();

            path = name != "index"
                ? FPath.Combine(path.GetDirectory(), name, index)
                : FPath.Combine(path.GetDirectory(), index);
        }

        return StepPaths.StripRoot(path, root);
    }

    /// <summary>
    /// The path of page <paramref name="page"/> of a paginated file whose pages all resolve to <paramref name="path"/>:
    /// <c>/a/index.html</c>, <c>/a/1/index.html</c>, <c>/a/2/index.html</c>... (<c>index.md</c> for Markdown).
    /// </summary>
    public static FPath Paged(FPath path, int page)
    {
        var name = Path.GetFileNameWithoutExtension(path.FullName);
        var directory = path.GetDirectory();
        var index = "index" + (path.GetExtensionWithDot() == Markdown ? Markdown : Html);

        if (name != "index")
        {
            directory = FPath.Combine(directory, name + "/");
        }

        return page == 0
            ? FPath.Combine(directory, index)
            : FPath.Combine(directory, page.ToString(CultureInfo.InvariantCulture) + "/", index);
    }
}
