using System.Net;
using System.Text.RegularExpressions;

namespace Douze.Workflow.Steps;

/// <summary>
/// Finds the code fences of a page as the <c>markdown</c> step writes them, for the steps that check fences (the hover
/// steps, <c>check-code</c>).
/// </summary>
internal static partial class CodeFenceScanner
{
    [GeneratedRegex(@"\Gpre((?:\s+data-(?:lang|meta)=""[^""]*"")*)\s*>")]
    private static partial Regex PreTag();

    [GeneratedRegex(@"\G<code(?:\s[^>]*)?>")]
    private static partial Regex CodeTag();

    [GeneratedRegex(@"data-(lang|meta)=""([^""]*)""")]
    private static partial Regex PreAttribute();

    /// <summary>
    /// The fences of a page that <paramref name="select"/> selects (by language and meta), as the <c>markdown</c> step
    /// writes fences: a <c>&lt;pre&gt;</c> with only <c>data-lang</c> and <c>data-meta</c> attributes whose only child
    /// is a <c>&lt;code&gt;</c> holding escaped text. Returns where the <c>&lt;pre</c> tag's attributes end, the
    /// language, the meta and the decoded code (without the last line end, tabs replaced by <paramref name="tabWidth"/>
    /// spaces unless it is 0, trimmed when <paramref name="trim"/> is true).
    /// </summary>
    public static IEnumerable<(int TagEnd, string Language, string Meta, string Code)> FindFences(string html, Func<string, string, bool> select, int tabWidth, bool trim)
    {
        var index = 0;

        while ((index = html.IndexOf("<pre", index, StringComparison.Ordinal)) >= 0)
        {
            var pre = PreTag().Match(html, index + 1);

            if (!pre.Success)
            {
                index += 4;
                continue;
            }

            var code = CodeTag().Match(html, pre.Index + pre.Length);
            var end = code.Success ? html.IndexOf("</code></pre>", code.Index + code.Length, StringComparison.Ordinal) : -1;

            if (end < 0)
            {
                index = pre.Index + pre.Length;
                continue;
            }

            var language = "";
            var meta = "";

            foreach (Match attribute in PreAttribute().Matches(pre.Groups[1].Value))
            {
                var value = WebUtility.HtmlDecode(attribute.Groups[2].Value);

                if (attribute.Groups[1].Value == "lang")
                {
                    language = value;
                }
                else
                {
                    meta = value;
                }
            }

            index = end;
            var text = html.AsSpan(code.Index + code.Length, end - code.Index - code.Length);

            if (!select(language, meta) || text.Contains('<'))
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(text.ToString()).ReplaceLineEndings("\n");

            if (decoded.EndsWith('\n'))
            {
                decoded = decoded[..^1];
            }

            if (tabWidth > 0)
            {
                decoded = decoded.Replace("\t", new string(' ', tabWidth), StringComparison.Ordinal);
            }

            if (trim)
            {
                decoded = TrimCode(decoded);
            }

            // The end of the attributes: before the '>' of the tag, and its spaces.
            var tagEnd = pre.Index + pre.Length - 1;

            while (char.IsWhiteSpace(html[tagEnd - 1]))
            {
                tagEnd--;
            }

            yield return (tagEnd, language, meta, decoded);
        }
    }

    /// <summary>
    /// The first line of a fence that is not blank, trimmed and shortened, to name the fence in an error.
    /// </summary>
    public static string FirstLine(string code)
    {
        var line = code.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }

    /// <summary>
    /// The code with the white space at the end of its lines and its leading and trailing blank lines removed.
    /// </summary>
    private static string TrimCode(string code)
    {
        var lines = code.Split('\n').Select(x => x.TrimEnd()).ToList();
        var start = lines.FindIndex(x => x.Length > 0);

        if (start < 0)
        {
            return "";
        }

        var end = lines.FindLastIndex(x => x.Length > 0);
        return string.Join('\n', lines.Skip(start).Take(end - start + 1));
    }
}
