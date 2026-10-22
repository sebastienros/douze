using Markdig;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Douze.Markdown;

/// <summary>
/// Turns the straight apostrophe of a word (<c>it's</c>, <c>80's</c>) into a typographic one (<c>’</c>) in the text of
/// a document, which Markdig's smart punctuation leaves alone: a <c>'</c> right after a letter or a digit, outside code,
/// HTML and raw regions. Added with the <c>smartypants</c> extension.
/// </summary>
internal sealed class ApostropheExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed -= Process;
        pipeline.DocumentProcessed += Process;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void Process(MarkdownDocument document)
    {
        foreach (var literal in document.Descendants<LiteralInline>())
        {
            var text = literal.Content;

            if (text.IndexOf('\'') < 0)
            {
                continue;
            }

            var span = text.AsSpan();
            var result = (char[]?)null;

            for (var i = 1; i < span.Length; i++)
            {
                if (span[i] == '\'' && char.IsLetterOrDigit(span[i - 1]))
                {
                    result ??= span.ToArray();
                    result[i] = '’';
                }
            }

            if (result != null)
            {
                literal.Content = new StringSlice(new string(result));
            }
        }
    }
}
