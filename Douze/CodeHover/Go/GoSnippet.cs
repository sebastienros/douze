using System.Text;
using System.Text.RegularExpressions;

namespace Douze.CodeHover.Go;

/// <summary>
/// A Go snippet as the file of a package of the module that holds every snippet of a check: its code, completed so that
/// it compiles, and the map between the two. Each snippet is a package of its own (a folder), so that the <c>main</c>
/// functions and the declarations of snippets don't collide; what a fragment lacks is added around it:
/// <list type="bullet">
/// <item>a snippet without a <c>package</c> clause gets <c>package main</c> first;</item>
/// <item>statements (what a fragment of a function body is) go in a function, <c>func _() { ... }</c>, with the
/// declarations before them (imports, types, constants, variables) left outside, and the function declarations after
/// them too.</item>
/// </list>
/// A snippet with a <c>package</c> clause is a file as it is. The wrapping only inserts lines between the parts of the
/// snippet: positions in the snippet map to the document and back.
/// </summary>
/// <remarks>
/// The parts are found with a scanner of Go's tokens (comments, interpreted and raw strings, runes, brackets), not a
/// parser: an item starts a line at bracket depth 0 (Go ends a statement at a line end), and its first word says what
/// it is.
/// </remarks>
internal sealed partial class GoSnippet
{
    /// <summary>
    /// What a snippet without a <c>package</c> clause starts with.
    /// </summary>
    public const string PackageClause = "package main\n";

    private const string FunctionStart = "func _() {\n";

    private const string FunctionEnd = "\n}\n";

    // The copied parts of the code: (offset in the document, offset in the code, length).
    private readonly List<(int Document, int Code, int Length)> _segments;

    private GoSnippet(string code, string document, List<(int, int, int)> segments)
    {
        Code = code;
        Document = document;
        _segments = segments;
    }

    public string Code { get; }

    /// <summary>
    /// The text of the snippet's file in its package.
    /// </summary>
    public string Document { get; }

    // A function declaration, with or without a receiver, rather than a function literal (func(x int) { ... }).
    [GeneratedRegex(@"\Gfunc\s*(?:\([^()]*\)\s*)?[\p{L}_][\p{L}\p{Nd}_]*\s*[\[(]")]
    private static partial Regex FunctionDeclaration();

    /// <summary>
    /// Wraps <paramref name="code"/> so that it compiles as the file of a package.
    /// </summary>
    public static GoSnippet Wrap(string code)
    {
        var (package, statements, functions) = Scan(code);
        var document = new StringBuilder(code.Length + 32);
        var segments = new List<(int, int, int)>();

        if (!package)
        {
            document.Append(PackageClause);
        }

        if (package || statements < 0)
        {
            Copy(0, code.Length);
        }
        else
        {
            var end = functions >= 0 ? functions : code.Length;
            Copy(0, statements);
            document.Append(FunctionStart);
            Copy(statements, end);
            document.Append(FunctionEnd);
            Copy(end, code.Length);
        }

        return new GoSnippet(code, document.ToString(), segments);

        void Copy(int start, int end)
        {
            if (end > start)
            {
                segments.Add((document.Length, start, end - start));
                document.Append(code, start, end - start);
            }
        }
    }

    /// <summary>
    /// The offset in the document of an offset of the code.
    /// </summary>
    public int ToDocument(int offset)
    {
        foreach (var (document, code, length) in _segments)
        {
            if (offset >= code && offset <= code + length)
            {
                return document + offset - code;
            }
        }

        return Document.Length;
    }

    /// <summary>
    /// The offset in the code of an offset of the document, or -1 when it is in the wrapping (with
    /// <paramref name="clamp"/>, the start of the code that follows instead).
    /// </summary>
    public int ToCode(int offset, bool clamp = false)
    {
        foreach (var (document, code, length) in _segments)
        {
            if (offset >= document && offset <= document + length)
            {
                return code + offset - document;
            }

            if (clamp && offset < document)
            {
                return code;
            }
        }

        return clamp ? Code.Length : -1;
    }

    /// <summary>
    /// Whether the code has a <c>package</c> clause, where its statements start and where the function declarations
    /// after them start (-1 when there are none). Both start a line, with the comments right above them.
    /// </summary>
    private static (bool Package, int Statements, int Functions) Scan(string code)
    {
        var statements = -1;
        var depth = 0;
        var atLineStart = true;
        var first = true;

        // The start of the line after the last line with code: where an item starts, with its comments.
        var itemStart = 0;
        var i = 0;

        while (i < code.Length)
        {
            var c = code[i];

            if (c == '\n')
            {
                if (!atLineStart)
                {
                    itemStart = i + 1;
                }

                atLineStart = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                i = code.IndexOf('\n', i) is var end and >= 0 ? end : code.Length;
                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : code.Length;
                continue;
            }

            if (atLineStart && depth == 0)
            {
                var word = Word(code, i);

                if (first && word == "package")
                {
                    return (true, -1, -1);
                }

                first = false;

                if (statements >= 0)
                {
                    if (word == "func" && FunctionDeclaration().IsMatch(code, i))
                    {
                        return (false, statements, itemStart);
                    }
                }
                else if (word is not ("import" or "type" or "const" or "var") && !(word == "func" && FunctionDeclaration().IsMatch(code, i)))
                {
                    statements = itemStart;
                }
            }

            atLineStart = false;

            switch (c)
            {
                case '"' or '\'':
                    i = SkipQuoted(code, i, c);
                    continue;
                case '`':
                    i = code.IndexOf('`', i + 1) is var end and >= 0 ? end + 1 : code.Length;
                    continue;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    if (char.IsLetter(c) || c == '_')
                    {
                        i += Word(code, i).Length;
                        continue;
                    }

                    break;
            }

            i++;
        }

        return (false, statements, -1);
    }

    private static string Word(string code, int index)
    {
        var end = index;

        while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
        {
            end++;
        }

        return code[index..end];
    }

    /// <summary>
    /// The end of an interpreted string or a rune: after its closing quote, or at the end of its line when it has
    /// none.
    /// </summary>
    private static int SkipQuoted(string code, int index, char quote)
    {
        for (var i = index + 1; i < code.Length; i++)
        {
            if (code[i] == '\\')
            {
                i++;
            }
            else if (code[i] == quote)
            {
                return i + 1;
            }
            else if (code[i] == '\n')
            {
                return i;
            }
        }

        return code.Length;
    }
}
