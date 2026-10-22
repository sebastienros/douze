using System.Collections.Frozen;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Douze.Markdown;

/// <summary>
/// Renders <c>:::note[Title]</c> containers (<c>customcontainers</c>) and <c>&gt; [!NOTE]</c> alerts (<c>alerts</c>) as
/// one markup (<see cref="AsideOptions"/>), in place of Markdig's renderers. A container's title is the text in brackets after its kind
/// (<c>:::tip[Before you start]</c>) or after a space (<c>:::tip Before you start</c>); an alert has the default title of
/// its kind. GitHub's alert kinds that are not aside kinds map to one: <c>important</c> to <c>note</c>, <c>warning</c>
/// to <c>caution</c>. Any other kind renders as the first kind (<c>note</c>) with a warning.
/// </summary>
internal sealed class AsideExtension(AsideOptions options) : IMarkdownExtension
{
    private static readonly FrozenDictionary<string, string> AlertKinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["important"] = "note",
        ["warning"] = "caution",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
        {
            return;
        }

        html.ObjectRenderers.TryRemove<HtmlCustomContainerRenderer>();
        html.ObjectRenderers.TryRemove<AlertBlockRenderer>();

        // Before the quote renderer, since an alert is a quote block.
        html.ObjectRenderers.Insert(0, new ContainerRenderer(this));
        html.ObjectRenderers.Insert(0, new AlertRenderer(this));
    }

    private void Write(HtmlRenderer renderer, ContainerBlock block, string kind, string? title, string? infoClass)
    {
        var state = (renderer as PooledHtmlRenderer)?.State;

        if (!options.Kinds.Contains(kind))
        {
            state?.Warn?.Invoke(new MarkdownWarning(block.Line + 1, block.Column + 1, $"unknown aside kind '{kind}'; rendered as '{options.Kinds[0]}'"));
            kind = options.Kinds[0];
        }

        title ??= state?.AsideTitles != null && state.AsideTitles.TryGetValue(kind, out var localized) ? localized : options.Title(kind);

        renderer.EnsureLine();

        if (!renderer.EnableHtmlForBlock)
        {
            renderer.WriteChildren(block);
            return;
        }

        var attributes = block.TryGetAttributes();
        var cls = options.Class;

        renderer.Write('<').Write(options.Tag);

        if (attributes?.Id is { } id)
        {
            renderer.Write(" id=\"").WriteEscape(id).Write('"');
        }

        renderer.Write(" aria-label=\"").WriteEscape(title).Write('"');
        renderer.Write(" class=\"").WriteEscape(cls).Write(' ').WriteEscape(cls).Write("--").WriteEscape(kind);

        if (attributes?.Classes is { } classes)
        {
            // Generic attributes ({.wide}); not the classes Markdig derives from the kind.
            foreach (var extra in classes)
            {
                if (extra != infoClass && !extra.StartsWith("markdown-alert", StringComparison.Ordinal))
                {
                    renderer.Write(' ').WriteEscape(extra);
                }
            }
        }

        renderer.Write('"');

        if (attributes?.Properties is { } properties)
        {
            foreach (var (name, value) in properties)
            {
                renderer.Write(' ').Write(name).Write("=\"").WriteEscape(value ?? "").Write('"');
            }
        }

        renderer.Write('>');
        renderer.Write("<p class=\"").WriteEscape(cls).Write("__title\" aria-hidden=\"true\">");

        if (options.Icons.TryGetValue(kind, out var icon))
        {
            renderer.Write(icon);
        }

        renderer.WriteEscape(title).WriteLine("</p>");
        renderer.Write("<div class=\"").WriteEscape(cls).WriteLine("__content\">");

        var implicitParagraph = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = false;
        renderer.WriteChildren(block);
        renderer.ImplicitParagraph = implicitParagraph;

        renderer.EnsureLine();
        renderer.WriteLine("</div>");
        renderer.Write("</").Write(options.Tag).WriteLine(">");
    }

    /// <summary>
    /// The kind and title of a container's info string: <c>note[Title]</c>, <c>note Title</c> or <c>note</c>.
    /// </summary>
    internal static (string Kind, string? Title) ParseInfo(string info)
    {
        info = info.Trim();

        var end = info.AsSpan().IndexOfAny('[', ' ', '\t');

        if (end < 0)
        {
            return (info.ToLowerInvariant(), null);
        }

        var kind = info[..end].ToLowerInvariant();
        var rest = info[end..].Trim();

        if (rest.StartsWith('[') && rest.LastIndexOf(']') is var close and > 0)
        {
            rest = rest[1..close].Trim();
        }

        return (kind, rest.Length == 0 ? null : rest);
    }

    private sealed class ContainerRenderer(AsideExtension extension) : HtmlObjectRenderer<CustomContainer>
    {
        protected override void Write(HtmlRenderer renderer, CustomContainer obj)
        {
            // Markdig splits the info string at the first space: "note[Two" and "words]".
            var info = string.IsNullOrEmpty(obj.Arguments) ? obj.Info ?? "" : obj.Info + " " + obj.Arguments;
            var (kind, title) = ParseInfo(info);

            extension.Write(renderer, obj, kind, title, obj.Info);
        }
    }

    private sealed class AlertRenderer(AsideExtension extension) : HtmlObjectRenderer<AlertBlock>
    {
        protected override void Write(HtmlRenderer renderer, AlertBlock obj)
        {
            var kind = obj.Kind.ToString().ToLowerInvariant();

            if (!extension.KnowsKind(kind) && AlertKinds.TryGetValue(kind, out var mapped))
            {
                kind = mapped;
            }

            extension.Write(renderer, obj, kind, null, null);
        }
    }

    private bool KnowsKind(string kind) => options.Kinds.Contains(kind);
}
