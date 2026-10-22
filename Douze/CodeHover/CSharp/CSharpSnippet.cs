using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Douze.CodeHover.CSharp;

/// <summary>
/// A C# snippet as a document of the project that holds every snippet of a check: its code, wrapped so that it compiles
/// next to the others, and the map between the two. A project has one file of top-level statements at most and one
/// declaration of each type, so each snippet is put in a namespace of its own, <c>__Snippet&lt;n&gt;</c>:
/// <list type="bullet">
/// <item>top-level statements (what most documentation snippets are) become the body of a method,
/// <c>__Program.__Main(string[] args)</c>, which is <c>async</c> so that <c>await</c> works; the type declarations
/// after them stay in the namespace. With a context class, <c>__Program</c> derives from it: its fields and
/// properties are the variables that fragments use without declaring them (the <c>builder</c> of an earlier
/// snippet), which a snippet's own locals hide;</item>
/// <item>type and namespace declarations stay as they are, in the namespace (a file-scoped namespace becomes a block
/// one, its <c>;</c> replaced by <c>{</c>: same length);</item>
/// <item>members without a type (a method, a property: fragments of a class) go in a class, <c>__Members</c>.</item>
/// </list>
/// The using directives at the top stay before the namespace. The wrapping only inserts text between the parts of the
/// snippet: positions in the snippet map to the document and back.
/// </summary>
/// <remarks>
/// The parts are found with a scanner of C#'s tokens (comments, strings of every kind, character literals, brackets),
/// not a parser: C# requires using directives first, then statements, then declarations, so the first item (at bracket
/// depth 0, after a <c>;</c> or a <c>}</c>) that starts a declaration starts them all.
/// </remarks>
internal sealed partial class CSharpSnippet
{
    /// <summary>
    /// The prefix of the namespaces of the snippets, which hovers do not show.
    /// </summary>
    public const string NamespacePrefix = "__Snippet";

    private const string ProgramStart = " { static class __Program { static async global::System.Threading.Tasks.Task __Main(string[] args) {\n";

    private const string ContextProgramStart = " { class __Program : {0} { async global::System.Threading.Tasks.Task __Main(string[] args) {\n";

    // The copied parts of the code: (offset in the document, offset in the code, length).
    private readonly List<(int Document, int Code, int Length)> _segments;

    private CSharpSnippet(string code, string document, List<(int, int, int)> segments)
    {
        Code = code;
        Document = document;
        _segments = segments;
    }

    public string Code { get; }

    /// <summary>
    /// The text of the snippet's file in the project.
    /// </summary>
    public string Document { get; }

    [GeneratedRegex(@"\G(?:global\s+)?using\s+(?:static\s+)?(?:@?[A-Za-z_][\w.]*\s*=\s*)?@?[A-Za-z_][\w.<>,\s:?\[\]]*;")]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"\Gextern\s+alias\s+\w+\s*;")]
    private static partial Regex ExternAlias();

    [GeneratedRegex(@"__Snippet\d+\.|__Program\.|__Members\.")]
    private static partial Regex WrapperNames();

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "static", "sealed", "abstract", "partial", "file", "readonly",
        "unsafe", "new", "ref", "virtual", "override", "extern", "async", "required", "const", "volatile",
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal) { "class", "struct", "interface", "enum", "record", "delegate" };

    private static readonly HashSet<string> AccessModifiers = new(StringComparer.Ordinal) { "public", "internal", "private", "protected" };

    private enum ItemKind { Using, Statement, Declaration, Member }

    /// <summary>
    /// Wraps <paramref name="code"/> as the <paramref name="index"/>th snippet of its project, its statements in a class
    /// that derives from <paramref name="context"/> when it is not null.
    /// </summary>
    public static CSharpSnippet Wrap(string code, int index, string? context = null)
    {
        var name = NamespacePrefix + index.ToString(CultureInfo.InvariantCulture);
        var (statements, declarations, members, fileScopedNamespace) = Scan(code);
        var document = new StringBuilder(code.Length + 160);
        var segments = new List<(int, int, int)>();

        if (members >= 0)
        {
            Copy(0, members);
            document.Append("namespace ").Append(name).Append(" { class __Members {\n");
            Copy(members, code.Length);
            document.Append("\n} }\n");
        }
        else if (statements >= 0)
        {
            var end = declarations >= 0 ? declarations : code.Length;
            Copy(0, statements);
            document.Append("namespace ").Append(name).Append(context == null ? ProgramStart : ContextProgramStart.Replace("{0}", "global::" + context, StringComparison.Ordinal));
            Copy(statements, end);
            document.Append("\n} }\n");
            Copy(end, code.Length);
            document.Append("\n}\n");
        }
        else if (declarations >= 0)
        {
            Copy(0, declarations);
            document.Append("namespace ").Append(name).Append(" {\n");
            Copy(declarations, code.Length);
            document.Append("\n}\n");
        }
        else
        {
            Copy(0, code.Length);
        }

        if (fileScopedNamespace >= 0)
        {
            // namespace X; -> namespace X { ... }, closed at the end.
            var at = new CSharpSnippet(code, document.ToString(), segments).ToDocument(fileScopedNamespace);
            document[at] = '{';
            document.Append("}\n");
        }

        return new CSharpSnippet(code, document.ToString(), segments);

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
    /// A hover's text without the names of the wrapping (<c>__Snippet3.Person</c> is <c>Person</c>).
    /// </summary>
    public static string Unwrap(string text) => WrapperNames().Replace(text, "");

    /// <summary>
    /// Where the statements, the declarations and the members start in the code (-1 when there are none), and the
    /// <c>;</c> of a file-scoped namespace.
    /// </summary>
    private static (int Statements, int Declarations, int Members, int FileScopedNamespace) Scan(string code)
    {
        var statements = -1;
        var depth = 0;
        var atItemStart = true;
        var i = 0;

        while (true)
        {
            var trivia = i;

            if ((i = SkipTrivia(code, i)) >= code.Length)
            {
                break;
            }

            if (depth == 0 && atItemStart)
            {
                atItemStart = false;

                // An item starts with the comments before it (its documentation), from the line after the previous one.
                var start = LineAfter(code, trivia);

                switch (Classify(code, i, statements >= 0))
                {
                    case (ItemKind.Using, var end):
                        i = end;
                        atItemStart = true;
                        continue;
                    case (ItemKind.Declaration, _):
                        return (statements, start, -1, FileScopedNamespace(code, i));
                    case (ItemKind.Member, _):
                        return (-1, -1, start, -1);
                    default:
                        statements = statements < 0 ? start : statements;
                        break;
                }
            }

            switch (code[i])
            {
                case '"' or '\'' or '@' or '$':
                    var end = SkipLiteral(code, i);

                    if (end > i)
                    {
                        i = end;
                        continue;
                    }

                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    atItemStart = depth == 0;
                    break;
                case ';':
                    atItemStart = depth == 0;
                    break;
            }

            i++;
        }

        return (statements, -1, -1, -1);
    }

    /// <summary>
    /// The start of the line after <paramref name="index"/> when only white space is between, or
    /// <paramref name="index"/>.
    /// </summary>
    private static int LineAfter(string code, int index)
    {
        for (var i = index; i < code.Length && char.IsWhiteSpace(code[i]); i++)
        {
            if (code[i] == '\n')
            {
                return i + 1;
            }
        }

        return index;
    }

    /// <summary>
    /// What the item at <paramref name="start"/> is, and where a using directive ends.
    /// </summary>
    private static (ItemKind Kind, int End) Classify(string code, int start, bool afterStatements)
    {
        if (!afterStatements)
        {
            var directive = UsingDirective().Match(code, start);

            if (!directive.Success)
            {
                directive = ExternAlias().Match(code, start);
            }

            if (directive.Success && !directive.Value.StartsWith("using var", StringComparison.Ordinal))
            {
                return (ItemKind.Using, directive.Index + directive.Length);
            }
        }

        // Attributes, then modifiers, then what is declared.
        var i = start;
        var hasAttributes = false;
        var hasAccess = false;

        while (true)
        {
            i = SkipTrivia(code, i);

            if (i < code.Length && code[i] == '[')
            {
                hasAttributes = true;
                i = SkipBrackets(code, i);
                continue;
            }

            var word = WordAt(code, i);

            if (word.Length == 0)
            {
                break;
            }

            if (word == "namespace" || TypeKeywords.Contains(word) && IsFollowedByName(code, i + word.Length))
            {
                return (ItemKind.Declaration, start);
            }

            if (!Modifiers.Contains(word))
            {
                break;
            }

            hasAccess |= AccessModifiers.Contains(word);
            i += word.Length;
        }

        // An access modifier on something that is not a type: a member (a local function cannot have one). Assembly
        // attributes alone are declarations.
        if (hasAccess && !afterStatements)
        {
            return (ItemKind.Member, start);
        }

        return hasAttributes && i >= code.Length ? (ItemKind.Declaration, start) : (ItemKind.Statement, start);
    }

    /// <summary>
    /// The <c>;</c> of the namespace declaration at <paramref name="start"/> when it is file-scoped, or -1.
    /// </summary>
    private static int FileScopedNamespace(string code, int start)
    {
        if (WordAt(code, start) != "namespace")
        {
            return -1;
        }

        for (var i = SkipTrivia(code, start); i < code.Length; i = SkipTrivia(code, i + 1))
        {
            if (code[i] == ';')
            {
                return i;
            }

            if (code[i] == '{')
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsFollowedByName(string code, int index)
    {
        var next = SkipTrivia(code, index);
        return next < code.Length && next > index && (char.IsLetter(code[next]) || code[next] is '_' or '@');
    }

    private static string WordAt(string code, int index)
    {
        var end = index;

        while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
        {
            end++;
        }

        return code[index..end];
    }

    /// <summary>
    /// The end of the brackets (attribute) at <paramref name="start"/>.
    /// </summary>
    private static int SkipBrackets(string code, int start)
    {
        var depth = 0;
        var i = start;

        while (i < code.Length)
        {
            var end = SkipLiteral(code, i);

            if (end > i)
            {
                i = end;
                continue;
            }

            if (code[i] == '[')
            {
                depth++;
            }
            else if (code[i] == ']' && --depth == 0)
            {
                return i + 1;
            }

            i++;
        }

        return code.Length;
    }

    /// <summary>
    /// The index of the first character at or after <paramref name="index"/> that is not white space, a comment or a
    /// preprocessor line.
    /// </summary>
    private static int SkipTrivia(string code, int index)
    {
        var i = index;

        while (i < code.Length)
        {
            if (char.IsWhiteSpace(code[i]))
            {
                i++;
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/' || code[i] == '#' && IsLineStart(code, i))
            {
                i = code.IndexOf('\n', i) is var end and >= 0 ? end + 1 : code.Length;
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : code.Length;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    private static bool IsLineStart(string code, int index)
    {
        for (var i = index - 1; i >= 0 && code[i] != '\n'; i--)
        {
            if (!char.IsWhiteSpace(code[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The end of the string or character literal at <paramref name="start"/> (regular, verbatim, interpolated, raw),
    /// or <paramref name="start"/> when there is none there.
    /// </summary>
    private static int SkipLiteral(string code, int start)
    {
        var i = start;
        var dollars = 0;
        var verbatim = false;

        while (i < code.Length && (code[i] == '$' || code[i] == '@'))
        {
            if (code[i] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            i++;
        }

        if (i >= code.Length)
        {
            return start;
        }

        if (code[i] == '\'' && i == start)
        {
            // A character literal: '\'' or 'x'.
            for (i++; i < code.Length && code[i] != '\'' && code[i] != '\n'; i++)
            {
                if (code[i] == '\\')
                {
                    i++;
                }
            }

            return Math.Min(i + 1, code.Length);
        }

        if (code[i] != '"' || (i > start && !verbatim && dollars == 0))
        {
            return start;
        }

        // A raw string: three quotes or more, closed by as many.
        var quotes = 0;

        while (i + quotes < code.Length && code[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3 && !verbatim)
        {
            var close = code.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? code.Length : close + quotes;
        }

        for (i++; i < code.Length; i++)
        {
            var c = code[i];

            if (c == '\\' && !verbatim)
            {
                i++;
            }
            else if (c == '"')
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i + 1;
            }
            else if (c == '{' && dollars > 0)
            {
                if (i + 1 < code.Length && code[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                i = SkipInterpolation(code, i + 1) - 1;
            }
            else if (c == '\n' && !verbatim)
            {
                return i;
            }
        }

        return code.Length;
    }

    /// <summary>
    /// The index after the <c>}</c> that closes an interpolation whose expression starts at <paramref name="start"/>.
    /// </summary>
    private static int SkipInterpolation(string code, int start)
    {
        var depth = 0;
        var i = start;

        while (i < code.Length)
        {
            var end = SkipLiteral(code, i);

            if (end > i)
            {
                i = end;
                continue;
            }

            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                if (depth-- == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return code.Length;
    }
}
