using System.Collections.Frozen;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Douze.Markdown;

/// <summary>
/// The markup of code blocks, which highlighters read: a fence renders as
/// <c>&lt;pre data-lang="csharp" data-meta="title=&amp;quot;Program.cs&amp;quot; {3-4}"&gt;&lt;code class="language-csharp"&gt;...&lt;/code&gt;&lt;/pre&gt;</c>,
/// where <c>data-lang</c> is the first word of the info string and <c>data-meta</c> the rest, both left out when empty.
/// A passthrough language (<c>mermaid</c>) renders as <c>&lt;pre class="mermaid"&gt;...&lt;/pre&gt;</c>, without a
/// <c>code</c> element, and an indented block as <c>&lt;pre&gt;&lt;code&gt;</c>. The content is always escaped.
/// </summary>
internal sealed class CodeFenceRenderer : HtmlObjectRenderer<CodeBlock>
{
    private readonly FrozenSet<string> _passthrough;

    public CodeFenceRenderer(IEnumerable<string> passthroughLanguages)
    {
        _passthrough = passthroughLanguages.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        renderer.EnsureLine();

        var fence = obj as FencedCodeBlock;
        var language = fence?.Info;
        var meta = fence?.Arguments;

        if (!string.IsNullOrEmpty(language) && _passthrough.Contains(language))
        {
            renderer.Write("<pre class=\"").WriteEscape(language.ToLowerInvariant()).Write("\">");
            renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true);
            renderer.WriteLine("</pre>");
            return;
        }

        renderer.Write("<pre");

        if (!string.IsNullOrEmpty(language))
        {
            renderer.Write(" data-lang=\"").WriteEscape(language).Write('"');
        }

        if (!string.IsNullOrEmpty(meta))
        {
            renderer.Write(" data-meta=\"").WriteEscape(meta).Write('"');
        }

        // The language class (with the configured prefix) and generic attributes are on the code element, as Markdig
        // writes them.
        renderer.Write("><code").WriteAttributes(obj).Write('>');
        renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true);
        renderer.WriteLine("</code></pre>");
    }

    /// <summary>
    /// Replaces Markdig's code block renderer, and sets the prefix of the language class.
    /// </summary>
    public sealed class Extension(string classPrefix, IEnumerable<string> passthroughLanguages) : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            if (pipeline.BlockParsers.Find<Markdig.Parsers.FencedCodeBlockParser>() is { } parser)
            {
                parser.InfoPrefix = classPrefix;
            }
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is HtmlRenderer html)
            {
                html.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new CodeFenceRenderer(passthroughLanguages));
            }
        }
    }
}
