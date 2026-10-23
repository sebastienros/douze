using System.Net;

namespace Douze.Markdown;

/// <summary>
/// A link of an HTML text: the url of an attribute, entities decoded, as written otherwise.
/// </summary>
/// <param name="Url">The url.</param>
/// <param name="Line">The line of the attribute's value, from 1.</param>
internal readonly record struct HtmlLink(string Url, int Line);

/// <summary>
/// Finds the links (<c>href</c>, <c>src</c>, <c>srcset</c>, <c>poster</c>, <c>data-src</c>) and the anchors (<c>id</c>,
/// and <c>name</c> on <c>&lt;a&gt;</c>) of HTML with a forward scan of its tags, not a parser, like
/// <see cref="HeadingScanner"/>. Comments and the content of <c>script</c>, <c>style</c> and <c>textarea</c> are
/// skipped: their text is not markup. The content of <c>pre</c> and <c>code</c> is scanned, since the markup of a sample
/// is escaped and the elements in it (a highlighted fence's) are the page's.
/// </summary>
internal static class LinkScanner
{
    private static readonly string[] RawTextElements = ["script", "style", "textarea"];

    public static void Scan(string html, List<HtmlLink> links, List<string> anchors)
    {
        var scan = new Scanner(html, links, anchors);
        var i = 0;

        while ((i = html.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            var nameStart = i + 1;
            var nameEnd = nameStart;

            if (nameEnd >= html.Length || !char.IsAsciiLetter(html[nameEnd]))
            {
                i++;
                continue;
            }

            while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-'))
            {
                nameEnd++;
            }

            var name = html.AsSpan(nameStart, nameEnd - nameStart);
            i = scan.ReadAttributes(nameEnd, isAnchor: name.Equals("a", StringComparison.OrdinalIgnoreCase));

            foreach (var raw in RawTextElements)
            {
                if (name.Equals(raw, StringComparison.OrdinalIgnoreCase))
                {
                    var close = html.IndexOf("</" + raw, i, StringComparison.OrdinalIgnoreCase);
                    i = close < 0 ? html.Length : close + 2 + raw.Length;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The urls of a <c>srcset</c>: candidates separated by commas, each a url (no whitespace) and an optional
    /// descriptor. A url may contain commas, but not end with one.
    /// </summary>
    internal static IEnumerable<string> SrcsetUrls(string srcset)
    {
        var i = 0;

        while (i < srcset.Length)
        {
            while (i < srcset.Length && (char.IsWhiteSpace(srcset[i]) || srcset[i] == ','))
            {
                i++;
            }

            var start = i;

            while (i < srcset.Length && !char.IsWhiteSpace(srcset[i]))
            {
                i++;
            }

            var url = srcset[start..i];
            var trimmed = url.TrimEnd(',');

            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }

            // A url ending with a comma ends its candidate; otherwise the descriptor runs to the next comma.
            if (trimmed.Length == url.Length)
            {
                while (i < srcset.Length && srcset[i] != ',')
                {
                    i++;
                }
            }
        }
    }

    private sealed class Scanner(string html, List<HtmlLink> links, List<string> anchors)
    {
        private int _line = 1;
        private int _lineOffset;

        /// <summary>
        /// Reads the attributes of an opening tag from <paramref name="i"/>, after its name. Returns the offset after
        /// the tag's <c>&gt;</c>, or the end of the text when it does not end.
        /// </summary>
        public int ReadAttributes(int i, bool isAnchor)
        {
            while (i < html.Length)
            {
                var c = html[i];

                if (c == '>')
                {
                    return i + 1;
                }

                if (char.IsWhiteSpace(c) || c == '/')
                {
                    i++;
                    continue;
                }

                var nameStart = i;

                while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
                {
                    i++;
                }

                var nameEnd = i;

                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i >= html.Length || html[i] != '=')
                {
                    continue;
                }

                i++;

                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                int valueStart, valueEnd;

                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var close = html.IndexOf(html[i], i + 1);

                    if (close < 0)
                    {
                        return html.Length;
                    }

                    valueStart = i + 1;
                    valueEnd = close;
                    i = close + 1;
                }
                else
                {
                    valueStart = i;

                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    valueEnd = i;
                }

                Attribute(html.AsSpan(nameStart, nameEnd - nameStart), valueStart, valueEnd, isAnchor);
            }

            return html.Length;
        }

        private void Attribute(ReadOnlySpan<char> name, int valueStart, int valueEnd, bool isAnchor)
        {
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase) || isAnchor && name.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (valueEnd > valueStart)
                {
                    anchors.Add(WebUtility.HtmlDecode(html[valueStart..valueEnd]));
                }

                return;
            }

            var isSrcset = name.Equals("srcset", StringComparison.OrdinalIgnoreCase);

            if (!isSrcset
                && !name.Equals("href", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("src", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("poster", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("data-src", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Lines are counted forward: attributes come in the order of the text.
            _line += html.AsSpan(_lineOffset, valueStart - _lineOffset).Count('\n');
            _lineOffset = valueStart;

            var value = WebUtility.HtmlDecode(html[valueStart..valueEnd]);

            if (isSrcset)
            {
                foreach (var url in SrcsetUrls(value))
                {
                    links.Add(new HtmlLink(url, _line));
                }
            }
            else if (value.Trim() is { Length: > 0 } url)
            {
                links.Add(new HtmlLink(url, _line));
            }
        }
    }
}
