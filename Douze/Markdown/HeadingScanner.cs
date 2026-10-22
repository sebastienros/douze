using System.Net;
using Cysharp.Text;

namespace Douze.Markdown;

/// <summary>
/// A heading of an HTML text.
/// </summary>
/// <param name="Depth">1 to 6.</param>
/// <param name="Text">The inner HTML without tags, entities decoded, whitespace collapsed.</param>
/// <param name="Id">The decoded value of its <c>id</c> attribute, or <c>null</c>.</param>
/// <param name="Start">The offset of <c>&lt;hN</c>.</param>
/// <param name="OpenTagEnd">The offset after the <c>&gt;</c> of the opening tag.</param>
/// <param name="CloseStart">The offset of <c>&lt;/hN&gt;</c>.</param>
/// <param name="End">The offset after <c>&lt;/hN&gt;</c>.</param>
internal readonly record struct Heading(int Depth, string Text, string? Id, int Start, int OpenTagEnd, int CloseStart, int End);

/// <summary>
/// Finds the headings of HTML with a forward scan, not a parser: the input is what Markdig, templates and tags produce,
/// regular markup. Headings inside <c>pre</c>, <c>code</c>, <c>script</c>, <c>style</c>, <c>template</c> and comments
/// are skipped.
/// </summary>
internal static class HeadingScanner
{
    private static readonly string[] SkippedElements = ["pre", "code", "script", "style", "template", "textarea"];

    public static List<Heading> Scan(string html)
    {
        var headings = new List<Heading>();
        var i = 0;

        while ((i = html.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            if (SkippedElement(html, i) is { } skipped)
            {
                var close = html.IndexOf("</" + skipped, i + 1 + skipped.Length, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close + 2 + skipped.Length;
                continue;
            }

            if (i + 3 <= html.Length && (html[i + 1] is 'h' or 'H') && html[i + 2] is >= '1' and <= '6' && IsNameEnd(html, i + 3))
            {
                var depth = html[i + 2] - '0';

                if (TryReadHeading(html, i, depth, out var heading))
                {
                    headings.Add(heading);
                    i = heading.End;
                    continue;
                }
            }

            i++;
        }

        return headings;
    }

    /// <summary>
    /// Rewrites <paramref name="html"/> in one pass: <paramref name="id"/> gives the id to add to a heading without
    /// one (<c>null</c>: none), <paramref name="append"/> the HTML to write at the end of a heading's content
    /// (<c>null</c>: none).
    /// </summary>
    public static string Rewrite(string html, IReadOnlyList<Heading> headings, Func<Heading, string?> id, Func<Heading, string?> append, Func<Heading, string?>? before = null, Func<Heading, string?>? after = null)
    {
        using var builder = ZString.CreateStringBuilder();
        var position = 0;

        foreach (var heading in headings)
        {
            builder.Append(html.AsSpan(position, heading.Start - position));

            if (before?.Invoke(heading) is { } opening)
            {
                builder.Append(opening);
            }

            // "<hN"
            builder.Append(html.AsSpan(heading.Start, 3));

            if (heading.Id == null && id(heading) is { } value)
            {
                builder.Append(" id=\"");
                builder.Append(EncodeAttribute(value));
                builder.Append('"');
            }

            builder.Append(html.AsSpan(heading.Start + 3, heading.CloseStart - heading.Start - 3));

            if (append(heading) is { } extra)
            {
                builder.Append(extra);
            }

            // "</hN>"
            builder.Append(html.AsSpan(heading.CloseStart, heading.End - heading.CloseStart));

            if (after?.Invoke(heading) is { } closing)
            {
                builder.Append(closing);
            }

            position = heading.End;
        }

        builder.Append(html.AsSpan(position));
        return builder.ToString();
    }

    private static bool TryReadHeading(string html, int start, int depth, out Heading heading)
    {
        heading = default;

        if (ReadOpenTag(html, start + 3, out var id) is not { } openTagEnd)
        {
            return false;
        }

        var closeTag = "</h" + (char)('0' + depth);
        var closeStart = openTagEnd;

        while (true)
        {
            closeStart = html.IndexOf(closeTag, closeStart, StringComparison.OrdinalIgnoreCase);

            if (closeStart < 0)
            {
                return false;
            }

            if (IsNameEnd(html, closeStart + closeTag.Length))
            {
                break;
            }

            closeStart++;
        }

        var end = html.IndexOf('>', closeStart);
        end = end < 0 ? html.Length : end + 1;

        heading = new Heading(depth, TextOf(html.AsSpan(openTagEnd, closeStart - openTagEnd)), id, start, openTagEnd, closeStart, end);
        return true;
    }

    /// <summary>
    /// Reads the attributes of an opening tag from <paramref name="i"/>, after its name, and returns the offset after
    /// its <c>&gt;</c>, or <c>null</c> when it does not end.
    /// </summary>
    private static int? ReadOpenTag(string html, int i, out string? id)
    {
        id = null;

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

            // An attribute name, then an optional value.
            var nameStart = i;

            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
            {
                i++;
            }

            var name = html.AsSpan(nameStart, i - nameStart);

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

            string value;

            if (i < html.Length && html[i] is '"' or '\'')
            {
                var quote = html[i];
                var close = html.IndexOf(quote, i + 1);

                if (close < 0)
                {
                    return null;
                }

                value = html[(i + 1)..close];
                i = close + 1;
            }
            else
            {
                var valueStart = i;

                while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                {
                    i++;
                }

                value = html[valueStart..i];
            }

            if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                id = WebUtility.HtmlDecode(value);
            }
        }

        return null;
    }

    /// <summary>
    /// The text of inner HTML: tags removed, entities decoded, whitespace collapsed and trimmed.
    /// </summary>
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\n', '\r', '\f', '\v'];

    internal static string TextOf(ReadOnlySpan<char> html)
    {
        using var builder = ZString.CreateStringBuilder();
        var inTag = false;

        foreach (var c in html)
        {
            if (inTag)
            {
                inTag = c != '>';
            }
            else if (c == '<')
            {
                inTag = true;
            }
            else
            {
                builder.Append(c);
            }
        }

        // Runs of ASCII whitespace collapse to one space; a no-break space is text (the slug of 3.3 drops it, as GitHub does).
        var decoded = WebUtility.HtmlDecode(builder.ToString());
        return string.Join(' ', decoded.Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Escapes <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and <c>"</c>, and nothing else (ids keep their letters).
    /// </summary>
    internal static string EncodeAttribute(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\"") < 0)
        {
            return value;
        }

        return value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string? SkippedElement(string html, int i)
    {
        foreach (var name in SkippedElements)
        {
            if (string.Compare(html, i + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0 && IsNameEnd(html, i + 1 + name.Length))
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsNameEnd(string html, int i) => i >= html.Length || char.IsWhiteSpace(html[i]) || html[i] is '>' or '/';
}
