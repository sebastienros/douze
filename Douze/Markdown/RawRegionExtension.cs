using System.Text;
using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Douze.Markdown;

/// <summary>
/// Raw regions: HTML that a Markdown pass keeps as it is. A region is a single line: <see cref="Marker"/> (U+001E, the
/// record separator, which no text contains), the HTML with every newline replaced by <see cref="NewLine"/> (U+001D),
/// and <see cref="Marker"/> again. Being one line, a region placed at the column of the tag that produced it stays in
/// the list item or block quote that holds the tag, and a blank line in its HTML cannot end an HTML block and turn the
/// rest into Markdown. The renderer writes the HTML with its newlines back, without escaping or processing.
/// </summary>
internal sealed class RawRegionExtension : IMarkdownExtension
{
    public const char Marker = '\u001E';

    public const char NewLine = '\u001D';

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<RawRegionParser>())
        {
            // First, so that no other parser sees the region lines.
            pipeline.BlockParsers.Insert(0, new RawRegionParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html && !html.ObjectRenderers.Contains<RawRegionRenderer>())
        {
            html.ObjectRenderers.Insert(0, new RawRegionRenderer());
        }
    }

    /// <summary>
    /// <paramref name="html"/> as a raw region: one line, without its trailing newlines. The two control characters
    /// of the encoding are removed from the HTML, which cannot contain them.
    /// </summary>
    public static string Encode(string html)
    {
        var span = html.AsSpan().TrimEnd("\r\n");
        var builder = new StringBuilder(span.Length + 2);
        builder.Append(Marker);

        foreach (var c in span)
        {
            switch (c)
            {
                case Marker or NewLine or '\r':
                    break;
                case '\n':
                    builder.Append(NewLine);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append(Marker);
        return builder.ToString();
    }

    /// <summary>
    /// Removes what is left of regions that did not start a line (a region inside a paragraph) from rendered HTML: the
    /// markers are removed and the encoded newlines restored.
    /// </summary>
    public static string StripMarkers(string html)
    {
        if (html.AsSpan().IndexOfAny(Marker, NewLine) < 0)
        {
            return html;
        }

        return html.Replace(Marker.ToString(), "", StringComparison.Ordinal).Replace(NewLine, '\n');
    }

    /// <summary>
    /// A raw region: <see cref="Html"/> is what it writes.
    /// </summary>
    internal sealed class RawRegionBlock(BlockParser parser) : LeafBlock(parser)
    {
        public string Html { get; init; } = "";
    }

    private sealed class RawRegionParser : BlockParser
    {
        public RawRegionParser()
        {
            OpeningCharacters = [Marker];
        }

        public override BlockState TryOpen(BlockProcessor processor)
        {
            var line = processor.Line.AsSpan().TrimEnd(" \t\r\n");

            if (line.Length < 2 || line[0] != Marker || line[^1] != Marker || line[1..^1].Contains(Marker))
            {
                return BlockState.None;
            }

            processor.NewBlocks.Push(new RawRegionBlock(this)
            {
                Column = processor.Column,
                Line = processor.LineIndex,
                Span = new SourceSpan(processor.Start, processor.Line.End),
                Html = line[1..^1].ToString().Replace(NewLine, '\n'),
            });

            return BlockState.BreakDiscard;
        }

        public override BlockState TryContinue(BlockProcessor processor, Block block) => BlockState.Break;
    }

    private sealed class RawRegionRenderer : HtmlObjectRenderer<RawRegionBlock>
    {
        protected override void Write(HtmlRenderer renderer, RawRegionBlock obj)
        {
            renderer.EnsureLine();
            renderer.Write(obj.Html);
            renderer.WriteLine();
        }
    }
}
