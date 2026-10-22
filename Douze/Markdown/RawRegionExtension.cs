using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Douze.Markdown;

/// <summary>
/// Raw regions: HTML that a Markdown pass keeps as it is. A line that is exactly <see cref="Marker"/> (U+001E, the
/// record separator, which no text contains) opens a region, the lines up to the next such line are its content, and
/// the content is written without escaping or processing. Tags that produce block HTML (shortcodes) write
/// <c>\n\u001E\n&lt;html&gt;\n\u001E\n</c>, so that a blank line in their output does not end an HTML block and turn the
/// rest into Markdown.
/// </summary>
internal sealed class RawRegionExtension : IMarkdownExtension
{
    public const char Marker = '\u001E';

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<RawRegionParser>())
        {
            // First, so that no other parser sees the marker lines.
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
    /// Removes the markers that did not delimit a region (a marker inside a line) from rendered HTML.
    /// </summary>
    public static string StripMarkers(string html)
    {
        return html.Contains(Marker) ? html.Replace(Marker.ToString(), "", StringComparison.Ordinal) : html;
    }

    private static bool IsMarkerLine(StringSlice line)
    {
        var span = line.AsSpan().Trim(" \t\r\n");
        return span is [Marker];
    }

    /// <summary>
    /// A raw region: its lines are the HTML to write.
    /// </summary>
    internal sealed class RawRegionBlock(BlockParser parser) : LeafBlock(parser)
    {
        /// <summary>
        /// Whether the closing marker line was found; a region left open runs to the end of its container.
        /// </summary>
        public bool IsClosed { get; set; }
    }

    private sealed class RawRegionParser : BlockParser
    {
        public RawRegionParser()
        {
            OpeningCharacters = [Marker];
        }

        public override BlockState TryOpen(BlockProcessor processor)
        {
            if (!IsMarkerLine(processor.Line))
            {
                return BlockState.None;
            }

            processor.NewBlocks.Push(new RawRegionBlock(this)
            {
                Column = processor.Column,
                Line = processor.LineIndex,
                Span = new SourceSpan(processor.Start, processor.Line.End),
            });

            // The marker line is not content.
            return BlockState.ContinueDiscard;
        }

        public override BlockState TryContinue(BlockProcessor processor, Block block)
        {
            processor.GoToColumn(processor.ColumnBeforeIndent);

            if (IsMarkerLine(processor.Line))
            {
                ((RawRegionBlock)block).IsClosed = true;
                block.UpdateSpanEnd(processor.Line.End);
                return BlockState.BreakDiscard;
            }

            return BlockState.Continue;
        }
    }

    private sealed class RawRegionRenderer : HtmlObjectRenderer<RawRegionBlock>
    {
        protected override void Write(HtmlRenderer renderer, RawRegionBlock obj)
        {
            if (!obj.IsClosed)
            {
                (renderer as PooledHtmlRenderer)?.State?.Warn?.Invoke(new MarkdownWarning(obj.Line + 1, obj.Column + 1, "a raw region is not closed"));
            }

            renderer.EnsureLine();
            renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: false);
        }
    }
}
