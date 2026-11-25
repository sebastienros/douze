using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Douze.DotNetApi;

/// <summary>
/// Turns the elements of a documentation comment (<c>summary</c>, <c>remarks</c>, <c>param</c>...) into HTML:
/// <c>para</c> is a paragraph, <c>c</c> is inline code, <c>code</c> a code block written as the <c>markdown</c> step
/// writes a fence (<c>&lt;pre data-lang="csharp"&gt;&lt;code class="language-csharp"&gt;</c>, the language of its
/// <c>language</c> attribute, C# by default), so that the <c>highlight</c> step colors it; <c>see</c> and <c>seealso</c> are links (a <c>cref</c>
/// through <see cref="CrefResolver"/>), <c>paramref</c> and <c>typeparamref</c> are code, <c>list</c> is a list or a
/// table, and the usual HTML elements (<c>b</c>, <c>i</c>, <c>a</c>, <c>br</c>, <c>ul</c>...) stay. Whitespace collapses
/// as HTML would, except in code blocks, whose common indentation is removed.
/// </summary>
internal sealed class DocHtml(DocHtml.CrefResolver resolve)
{
    /// <summary>
    /// The text and the url (null when the target has no page) of a <c>cref</c>.
    /// </summary>
    public delegate (string Text, string? Url) CrefResolver(string cref);

    private static readonly HashSet<string> InlineTags = new(StringComparer.Ordinal)
    {
        "b", "strong", "i", "em", "u", "sub", "sup", "small", "del", "s", "kbd", "var", "mark", "span",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th", "blockquote", "div", "dl", "dt", "dd",
    };

    /// <summary>
    /// The content of <paramref name="element"/> as HTML, or null when it has none.
    /// </summary>
    public string? Render(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        var writer = new Writer();
        Nodes(element, writer);
        var html = writer.ToString();
        return html.Length == 0 ? null : html;
    }

    private void Nodes(XElement parent, Writer writer)
    {
        foreach (var node in parent.Nodes())
        {
            Node(node, writer);
        }
    }

    private void Node(XNode node, Writer writer)
    {
        switch (node)
        {
            case XText text:
                writer.Text(text.Value);
                break;
            case XElement element:
                Element(element, writer);
                break;
        }
    }

    /// <summary>
    /// A <c>&lt;para&gt;</c>: a paragraph, or one per run of inline content around the blocks it has (a
    /// <c>&lt;code&gt;</c> or a <c>&lt;list&gt;</c>), which HTML does not allow in a paragraph.
    /// </summary>
    private void Paragraph(XElement element, Writer writer)
    {
        var run = new List<XNode>();

        foreach (var node in element.Nodes())
        {
            if (node is XElement child && IsBlock(child.Name.LocalName))
            {
                Run(run, writer);
                Element(child, writer);
            }
            else
            {
                run.Add(node);
            }
        }

        Run(run, writer);
    }

    private void Run(List<XNode> run, Writer writer)
    {
        if (run.Any(x => x is XElement || x is XText text && !string.IsNullOrWhiteSpace(text.Value)))
        {
            writer.Block("<p>");

            foreach (var node in run)
            {
                Node(node, writer);
            }

            writer.Block("</p>");
        }

        run.Clear();
    }

    private static bool IsBlock(string tag) => tag is "para" or "p" or "code" or "list" or "note" || BlockTags.Contains(tag);

    private void Element(XElement element, Writer writer)
    {
        var tag = element.Name.LocalName;

        switch (tag)
        {
            case "para" or "p":
                Paragraph(element, writer);
                break;

            case "c":
                writer.Inline("<code>");
                writer.Raw(WebUtility.HtmlEncode(Collapse(element.Value)));
                writer.Inline("</code>");
                break;

            case "code":
                var language = (string?)element.Attribute("language") ?? (string?)element.Attribute("lang") ?? "csharp";
                // As the markdown step writes a fence, so that the highlight step colors it.
                var encoded = WebUtility.HtmlEncode(language);
                writer.Block($"<pre data-lang=\"{encoded}\"><code class=\"language-{encoded}\">");
                writer.Raw(WebUtility.HtmlEncode(Dedent(element.Value)));
                writer.Block("</code></pre>");
                break;

            case "see" or "seealso":
                Link(element, writer);
                break;

            case "paramref" or "typeparamref":
                writer.Inline("<code>");
                writer.Raw(WebUtility.HtmlEncode((string?)element.Attribute("name") ?? ""));
                writer.Inline("</code>");
                break;

            case "list":
                List(element, writer);
                break;

            case "br":
                writer.Inline("<br>");
                break;

            case "a":
                writer.Inline($"<a href=\"{WebUtility.HtmlEncode((string?)element.Attribute("href") ?? "")}\">");
                Nodes(element, writer);
                writer.Inline("</a>");
                break;

            case "note":
                writer.Block("<div class=\"note\">");
                Nodes(element, writer);
                writer.Block("</div>");
                break;

            case var _ when InlineTags.Contains(tag):
                writer.Inline($"<{tag}>");
                Nodes(element, writer);
                writer.Inline($"</{tag}>");
                break;

            case var _ when BlockTags.Contains(tag):
                writer.Block($"<{tag}>");
                Nodes(element, writer);
                writer.Block($"</{tag}>");
                break;

            default:
                Nodes(element, writer);
                break;
        }
    }

    private void Link(XElement element, Writer writer)
    {
        if ((string?)element.Attribute("cref") is { Length: > 0 } cref)
        {
            var (text, url) = resolve(cref);
            var custom = element.Nodes().Any();

            if (url != null)
            {
                writer.Inline($"<a href=\"{WebUtility.HtmlEncode(url)}\">");
            }

            if (custom)
            {
                Nodes(element, writer);
            }
            else
            {
                writer.Inline("<code>");
                writer.Raw(WebUtility.HtmlEncode(text));
                writer.Inline("</code>");
            }

            if (url != null)
            {
                writer.Inline("</a>");
            }
        }
        else if ((string?)element.Attribute("langword") is { Length: > 0 } keyword)
        {
            writer.Inline("<code>");
            writer.Raw(WebUtility.HtmlEncode(keyword));
            writer.Inline("</code>");
        }
        else if ((string?)element.Attribute("href") is { Length: > 0 } href)
        {
            writer.Inline($"<a href=\"{WebUtility.HtmlEncode(href)}\">");

            if (element.Nodes().Any())
            {
                Nodes(element, writer);
            }
            else
            {
                writer.Raw(WebUtility.HtmlEncode(href));
            }

            writer.Inline("</a>");
        }
        else
        {
            Nodes(element, writer);
        }
    }

    private void List(XElement list, Writer writer)
    {
        var type = (string?)list.Attribute("type") ?? "bullet";

        if (type == "table")
        {
            writer.Block("<table>");

            if (list.Element("listheader") is { } header)
            {
                writer.Block("<thead>");
                Row(header, writer, "th");
                writer.Block("</thead>");
            }

            writer.Block("<tbody>");

            foreach (var item in list.Elements("item"))
            {
                Row(item, writer, "td");
            }

            writer.Block("</tbody>");
            writer.Block("</table>");
            return;
        }

        var listTag = type == "number" ? "ol" : "ul";
        writer.Block($"<{listTag}>");

        foreach (var item in list.Elements("item"))
        {
            writer.Block("<li>");
            var term = item.Element("term");
            var description = item.Element("description");

            if (term != null)
            {
                writer.Inline("<strong>");
                Nodes(term, writer);
                writer.Inline("</strong>");

                if (description != null)
                {
                    writer.Text(" – ");
                }
            }

            if (description != null)
            {
                Nodes(description, writer);
            }
            else if (term == null)
            {
                Nodes(item, writer);
            }

            writer.Block("</li>");
        }

        writer.Block($"</{listTag}>");
    }

    private void Row(XElement item, Writer writer, string cell)
    {
        writer.Block("<tr>");
        var cells = item.Elements().Where(x => x.Name.LocalName is "term" or "description").ToList();

        if (cells.Count == 0)
        {
            writer.Block($"<{cell}>");
            Nodes(item, writer);
            writer.Block($"</{cell}>");
        }

        foreach (var element in cells)
        {
            writer.Block($"<{cell}>");
            Nodes(element, writer);
            writer.Block($"</{cell}>");
        }

        writer.Block("</tr>");
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The lines of a code block without their common indentation and without the blank lines around them.
    /// </summary>
    internal static string Dedent(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n').ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var indent = lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Length - x.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(x => x.Length >= indent ? x[indent..].TrimEnd() : x.Trim()));
    }

    /// <summary>
    /// Accumulates HTML: text collapses its whitespace, and none is kept at the edges of block elements.
    /// </summary>
    private sealed class Writer
    {
        private readonly StringBuilder _builder = new();
        private bool _blockStart = true;

        public void Text(string text)
        {
            var collapsed = new StringBuilder(text.Length);
            var space = false;

            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = true;
                    continue;
                }

                if (space)
                {
                    collapsed.Append(' ');
                }

                space = false;
                collapsed.Append(c);
            }

            if (space)
            {
                collapsed.Append(' ');
            }

            var value = collapsed.ToString();

            if (_blockStart || _builder.Length > 0 && _builder[^1] == ' ')
            {
                value = value.TrimStart();
            }

            if (value.Length == 0)
            {
                return;
            }

            _builder.Append(WebUtility.HtmlEncode(value));
            _blockStart = false;
        }

        public void Inline(string html)
        {
            _builder.Append(html);
            _blockStart = false;
        }

        public void Raw(string html)
        {
            _builder.Append(html);
            _blockStart = false;
        }

        public void Block(string html)
        {
            TrimEnd();
            _builder.Append(html);
            _blockStart = true;
        }

        private void TrimEnd()
        {
            while (_builder.Length > 0 && _builder[^1] == ' ')
            {
                _builder.Length--;
            }
        }

        public override string ToString()
        {
            TrimEnd();
            return _builder.ToString().Trim();
        }
    }
}
