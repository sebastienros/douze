using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Markdown;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;
using Fluid.Ast;
using Fluid.Values;

namespace Douze.Liquid.Tags;

/// <summary>
/// <c>{% markdown %}</c>, <c>{% include_markdown %}</c> and <c>{% slot %}</c>.
/// </summary>
internal static class MarkdownTags
{
    /// <summary>
    /// The deepest chain of Markdown includes.
    /// </summary>
    public const int MaxIncludeDepth = 16;

    public static readonly string[] Names = ["markdown", "include_markdown", "slot"];

    public static void Register(DouzeParser parser)
    {
        parser.RegisterParserBlock("markdown", parser.TagPosition, RenderMarkdownAsync);
        parser.RegisterParserTag("include_markdown", parser.TagArguments, IncludeMarkdownAsync);
        parser.RegisterParserBlock("slot", parser.TagArguments, RenderSlotAsync);
    }

    /// <summary>
    /// <c>{% markdown %}...{% endmarkdown %}</c>: the body rendered as Markdown to HTML; in a Markdown page, written as
    /// a raw region.
    /// </summary>
    private static async ValueTask<Completion> RenderMarkdownAsync(TagPosition position, IReadOnlyList<Statement> statements, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var (source, completion) = await TagRendering.RenderBodyAsync(statements, encoder, context);
        var html = await TagRendering.RenderMarkdownAsync(source, context, inline: false, "markdown");

        output.Write(TagRendering.IsMarkdown(context) ? RawRegionExtension.Encode(html) : html);
        return completion;
    }

    /// <summary>
    /// <c>{% slot "name" %}...{% endslot %}</c>: <c>&lt;div data-slot="name"&gt;body&lt;/div&gt;</c>, the body rendered
    /// like a shortcode's (to HTML in a Markdown page), so that a shortcode template can place the parts of its body.
    /// </summary>
    private static async ValueTask<Completion> RenderSlotAsync(TagArguments call, IReadOnlyList<Statement> statements, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        if (call.Arguments is not [{ Name: null } nameArgument])
        {
            throw new TemplateRenderException(TagRendering.TemplatePath(context), call.Position.Line, call.Position.Column, "slot takes a name: {% slot \"name\" %}");
        }

        var name = (await nameArgument.Expression.EvaluateAsync(context)).ToStringValue(context);
        var (source, completion) = await TagRendering.RenderBodyAsync(statements, encoder, context);
        var markdown = TagRendering.IsMarkdown(context);
        var body = markdown ? await TagRendering.RenderMarkdownAsync(source, context, inline: false, "slot") : source;

        var html = $"<div data-slot=\"{WebUtility.HtmlEncode(name)}\">{body}</div>";

        output.Write(markdown ? RawRegionExtension.Encode(html) : html);
        return completion;
    }

    /// <summary>
    /// <c>{% include_markdown "path", key: value %}</c>: a Markdown file of the content folder (or relative to the
    /// including file with <c>./</c> and <c>../</c>) rendered as a Liquid template with the page's context, its front
    /// matter values below the page's names and the named arguments above them. In a Markdown page the result is
    /// Markdown source, indented like the tag so that it stays in the tag's list item; elsewhere it is rendered to
    /// HTML.
    /// </summary>
    private static async ValueTask<Completion> IncludeMarkdownAsync(TagArguments call, IFluidOutput output, TextEncoder encoder, TemplateContext context)
    {
        var step = TagRendering.RequireStep(context, "include_markdown");

        if (call.Arguments.Count == 0 || call.Arguments[0].Name != null || call.Arguments.Skip(1).Any(x => x.Name == null))
        {
            throw new TemplateRenderException(TagRendering.TemplatePath(context), call.Position.Line, call.Position.Column, "include_markdown takes a path and named arguments: {% include_markdown \"path.md\", name: value %}");
        }

        var argument = (await call.Arguments[0].Expression.EvaluateAsync(context)).ToStringValue(context);
        var chain = context.AmbientValues.TryGetValue(AmbientKeys.Includes, out var value) && value is IReadOnlyList<FPath> includes ? includes : [];

        if (chain.Count >= MaxIncludeDepth)
        {
            throw new DouzeException($"include_markdown: more than {MaxIncludeDepth} nested includes: {string.Join(" -> ", chain)} -> {argument}");
        }

        var (path, folder) = Resolve(argument, chain, context, step);
        var deps = TagRendering.Dependencies(context) ?? step.Dependencies;

        if (!(await step.Build.Source.GetFileEntryAsync(path)).Exists)
        {
            deps.RecordTemplate(path, DependencySet.Absent);
            throw new DouzeException($"include_markdown: '{argument}' not found in '{folder}'");
        }

        byte[] bytes;

        await using (var stream = await step.Build.Source.OpenFileAsync(path))
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory, context.CancellationToken);
            bytes = memory.ToArray();
        }

        deps.RecordTemplate(path, Hash128.Of(bytes));

        var text = await FileContent.FromBytes(bytes, isText: true).ReadTextAsync(context.CancellationToken);
        var (frontMatter, body) = FrontMatter.Parse(text);
        var protectedBody = FenceTable.Protect(body, out var fences);
        var template = step.Build.Templates.GetOrParse(Hash128.OfText(protectedBody), protectedBody, path.FullName);

        var markdownPage = TagRendering.IsMarkdown(context);
        string markdown;

        using (context.EnterScope())
        {
            // The include's front matter is below the page's names: a value is only set when nothing defines it.
            foreach (var property in frontMatter.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!await IsDefinedAsync(property.Key, context))
                {
                    context.SetValue(property.Key, NodeValues.ToFluidValue(property.Value, null));
                }
            }

            foreach (var named in call.Arguments.Skip(1))
            {
                context.SetValue(named.Name!, await named.Expression.EvaluateAsync(context));
            }

            using (TagRendering.SetAmbient(context, AmbientKeys.Markdown, true))
            using (TagRendering.SetAmbient(context, AmbientKeys.Fences, fences))
            using (TagRendering.SetAmbient(context, AmbientKeys.TemplatePath, path.FullName))
            using (TagRendering.SetAmbient(context, AmbientKeys.Includes, (IReadOnlyList<FPath>)[.. chain, path]))
            {
                markdown = await template.Template.RenderAsync(context, encoder, isolateContext: false);
            }
        }

        markdown = fences?.Restore(markdown) ?? markdown;

        if (markdownPage)
        {
            output.Write(Indent(markdown, call.Position.ContainerPrefix));
        }
        else
        {
            output.Write(await TagRendering.RenderMarkdownAsync(markdown, context, inline: false, "include_markdown"));
        }

        return Completion.Normal;
    }

    /// <summary>
    /// The file an include names and the folder it was looked for in: relative to the including file (the page, or
    /// the include that includes it) with <c>./</c> and <c>../</c>, relative to the content folder otherwise.
    /// </summary>
    private static (FPath Path, FPath Folder) Resolve(string argument, IReadOnlyList<FPath> chain, TemplateContext context, StepContext step)
    {
        var folder = step.Build.Site.ContentFolder;

        if (argument.StartsWith("./", StringComparison.Ordinal) || argument.StartsWith("../", StringComparison.Ordinal))
        {
            var including = chain.Count > 0 ? chain[^1] : TagRendering.File(context) is { } file ? file.SourcePath ?? file.Path : (FPath?)null;

            if (including is { } includingPath)
            {
                folder = includingPath.GetDirectory();
            }
        }

        var path = LinkRewriter.Resolve(folder, argument) ?? throw new DouzeException($"include_markdown: '{argument}' is outside the source folder");
        return (path, folder);
    }

    private static async ValueTask<bool> IsDefinedAsync(string name, TemplateContext context)
    {
        if (!context.LocalScope.GetValue(name).IsNil())
        {
            return true;
        }

        var value = context.Model switch
        {
            MetadataValue metadata => metadata.GetValueOrNil(name, context),
            { } model => await model.GetValueAsync(name, context),
            _ => NilValue.Instance,
        };

        return !value.IsNil();
    }

    /// <summary>
    /// <paramref name="markdown"/> with <paramref name="prefix"/> at the start of every line but the first, which
    /// starts where the tag was.
    /// </summary>
    private static string Indent(string markdown, string prefix)
    {
        if (prefix.Length == 0 || !markdown.Contains('\n'))
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length + prefix.Length * 16);
        var start = 0;

        while (true)
        {
            var end = markdown.IndexOf('\n', start);

            if (end < 0)
            {
                builder.Append(markdown, start, markdown.Length - start);
                break;
            }

            builder.Append(markdown, start, end + 1 - start).Append(prefix);
            start = end + 1;
        }

        return builder.ToString();
    }
}
