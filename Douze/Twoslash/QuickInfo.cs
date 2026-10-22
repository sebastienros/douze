using System.Text;
using System.Text.RegularExpressions;

namespace Douze.Twoslash;

/// <summary>
/// The quick info of a hover in twoslash's terms: the signature (<see cref="Text"/>, the display parts of TypeScript's
/// quick info), the documentation and the JSDoc tags, read back from the Markdown of a TypeScript 7 language server
/// hover, which is
/// <code>
/// ```typescript
/// signature
/// ```
/// documentation
///
/// *@tag* `name` — text
/// </code>
/// Tags are read the way TypeScript's quick info gives them: the text of a <c>@param</c> starts with the parameter
/// name. Links to declarations (<c>[Name](file:///...)</c>) become the <c>{@link Name}</c> parts TypeScript's quick
/// info has, as twoslash joins them.
/// </summary>
internal sealed partial record QuickInfo(string Text, string? Docs, IReadOnlyList<(string Name, string? Text)>? Tags)
{
    [GeneratedRegex(@"\n\n\*@([^*\s]+)\*")]
    private static partial Regex TagStart();

    [GeneratedRegex(@"\[([^\]\n]*)\]\(file://[^)\s]*\)")]
    private static partial Regex DeclarationLink();

    /// <summary>
    /// Reads a hover's Markdown, or returns null when it has no code block (an empty hover).
    /// </summary>
    public static QuickInfo? Parse(string markdown)
    {
        // The opening fence: three backticks or more (more when the signature has three), and the language.
        var ticks = 0;

        while (ticks < markdown.Length && markdown[ticks] == '`')
        {
            ticks++;
        }

        if (ticks < 3)
        {
            return null;
        }

        var firstLine = markdown.IndexOf('\n', ticks);

        if (firstLine < 0)
        {
            return null;
        }

        var fence = "\n" + new string('`', ticks) + "\n";
        var close = markdown.IndexOf(fence, firstLine, StringComparison.Ordinal);

        if (close < 0)
        {
            return null;
        }

        var text = markdown[(firstLine + 1)..close];
        var documentation = markdown[(close + fence.Length)..];

        var tagMatches = TagStart().Matches(documentation);
        var docs = tagMatches.Count == 0 ? documentation : documentation[..tagMatches[0].Index];
        List<(string, string?)>? tags = null;

        for (var i = 0; i < tagMatches.Count; i++)
        {
            var match = tagMatches[i];
            var bodyEnd = i + 1 < tagMatches.Count ? tagMatches[i + 1].Index : documentation.Length;
            var name = match.Groups[1].Value;
            tags ??= [];
            tags.Add((name, TagText(name, documentation[(match.Index + match.Length)..bodyEnd])));
        }

        return new QuickInfo(text, docs.Length == 0 ? null : Links(docs), tags);
    }

    /// <summary>
    /// The text TypeScript's quick info gives a tag from the hover's rendering of it: <c> `name` — text</c> for a
    /// parameter or a property (the text is <c>name text</c>), <c> — text</c> or <c> - text</c> otherwise.
    /// </summary>
    private static string? TagText(string tag, string body)
    {
        var result = new StringBuilder();
        var rest = body;

        if (tag is "param" or "arg" or "argument" or "property" or "prop" or "template" or "augments" or "extends" && rest.StartsWith(" `", StringComparison.Ordinal))
        {
            var end = rest.IndexOf('`', 2);

            if (end > 0)
            {
                result.Append(rest, 2, end - 2);
                rest = rest[(end + 1)..];
            }
        }

        if (tag == "example")
        {
            // A caption, then the code in a tsx fence when it was not fenced already.
            var caption = "";

            if (rest.StartsWith(" — ", StringComparison.Ordinal))
            {
                var newline = rest.IndexOf('\n', StringComparison.Ordinal);
                caption = "<caption>" + rest[3..(newline < 0 ? rest.Length : newline)] + "</caption>";
                rest = newline < 0 ? "" : rest[newline..];
            }

            rest = rest.StartsWith('\n') ? rest[1..] : rest;

            if (rest.StartsWith("```tsx\n", StringComparison.Ordinal) && rest.EndsWith("\n```\n", StringComparison.Ordinal))
            {
                rest = rest[7..^5];
            }
            else if (rest.EndsWith('\n'))
            {
                rest = rest[..^1];
            }

            return caption.Length == 0 ? rest : caption + rest;
        }

        if (rest.StartsWith(" — ", StringComparison.Ordinal))
        {
            rest = rest[3..];
        }
        else if (rest.StartsWith(' '))
        {
            rest = rest[1..];
        }

        if (rest.Length > 0)
        {
            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(Links(rest));
        }

        return result.Length == 0 ? null : result.ToString();
    }

    /// <summary>
    /// Turns the declaration links of documentation back into the parts of TypeScript's quick info, joined by line
    /// breaks as twoslash joins them: <c>See \n{@link \nName\n}\n here</c>.
    /// </summary>
    private static string Links(string text)
    {
        if (!text.Contains("](file://", StringComparison.Ordinal))
        {
            return text;
        }

        return DeclarationLink().Replace(text, match =>
        {
            var before = match.Index > 0 ? "\n" : "";
            var after = match.Index + match.Length < text.Length ? "\n" : "";
            return $"{before}{{@link \n{match.Groups[1].Value}\n}}{after}";
        });
    }
}
