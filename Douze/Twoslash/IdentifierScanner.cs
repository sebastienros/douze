using System.Globalization;

namespace Douze.Twoslash;

/// <summary>
/// Finds the words of TypeScript or JavaScript code that can be identifiers: what twoslash hovers (every
/// <c>Identifier</c> node of the syntax tree), approximated by a scanner. It skips comments, strings, template text,
/// regular expression literals, numbers, private names (<c>#x</c>) and JSX text, and keeps keywords too, since a
/// keyword is an identifier where a name is expected (<c>x.default</c>, <c>{ type: 1 }</c>): the language service
/// tells them apart, a keyword has no quick info.
/// </summary>
internal static class IdentifierScanner
{
    /// <summary>
    /// A word: its offset, length and whether it follows <c>.</c> or <c>?.</c> (a property name).
    /// </summary>
    public readonly record struct Word(int Start, int Length, bool AfterDot);

    // After these words an expression starts: a slash starts a regular expression.
    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.Ordinal)
    {
        "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await", "extends", "satisfies", "as",
    };

    private enum Previous { None, Value, Operator, Dot }

    public static List<Word> Scan(string text, bool jsx)
    {
        var scanner = new Scanner(text, jsx);
        var position = 0;

        // A hashbang line.
        if (text.StartsWith("#!", StringComparison.Ordinal))
        {
            position = text.IndexOf('\n') is var end and >= 0 ? end : text.Length;
        }

        scanner.ScanCode(ref position, stopAtBrace: false);
        return scanner.Words;
    }

    private sealed class Scanner(string text, bool jsx)
    {
        public List<Word> Words { get; } = [];

        private Previous _previous = Previous.None;

        /// <summary>
        /// Scans code up to the end, or up to the <c>}</c> that closes the expression it is in when
        /// <paramref name="stopAtBrace"/> (a template substitution, a JSX expression), which it consumes.
        /// </summary>
        public void ScanCode(ref int i, bool stopAtBrace)
        {
            var depth = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' || char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '/' && Peek(i + 1) == '/')
                {
                    i = LineEnd(i);
                    continue;
                }

                if (c == '/' && Peek(i + 1) == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    i = SkipString(i, c);
                    _previous = Previous.Value;
                    continue;
                }

                if (c == '`')
                {
                    ScanTemplate(ref i);
                    _previous = Previous.Value;
                    continue;
                }

                if (IsIdentifierStart(c) || (c == '\\' && Peek(i + 1) == 'u'))
                {
                    var start = i;
                    i = IdentifierEnd(i);
                    var word = text[start..i];
                    Words.Add(new Word(start, i - start, _previous == Previous.Dot));
                    _previous = _previous != Previous.Dot && ExpressionKeywords.Contains(word) ? Previous.Operator : Previous.Value;
                    continue;
                }

                if (c == '#')
                {
                    // A private name: not an identifier.
                    i = IdentifierEnd(i + 1);
                    _previous = Previous.Value;
                    continue;
                }

                if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(i + 1))))
                {
                    i = NumberEnd(i);
                    _previous = Previous.Value;
                    continue;
                }

                if (c == '.' && Peek(i + 1) == '.' && Peek(i + 2) == '.')
                {
                    i += 3;
                    _previous = Previous.Operator;
                    continue;
                }

                if (c == '.' || (c == '?' && Peek(i + 1) == '.' && !char.IsAsciiDigit(Peek(i + 2))))
                {
                    i += c == '.' ? 1 : 2;
                    _previous = Previous.Dot;
                    continue;
                }

                if (c == '/' && _previous != Previous.Value)
                {
                    i = SkipRegex(i);
                    _previous = Previous.Value;
                    continue;
                }

                if (c == '<' && jsx && _previous != Previous.Value && IsJsxStart(i))
                {
                    ScanJsxElement(ref i);
                    _previous = Previous.Value;
                    continue;
                }

                switch (c)
                {
                    case '{' or '(' or '[':
                        if (c == '{')
                        {
                            depth++;
                        }

                        _previous = Previous.Operator;
                        break;

                    case '}':
                        if (depth == 0 && stopAtBrace)
                        {
                            i++;
                            _previous = Previous.Value;
                            return;
                        }

                        depth--;
                        _previous = Previous.Value;
                        break;

                    case ')' or ']':
                        _previous = Previous.Value;
                        break;

                    case '+' or '-' when Peek(i + 1) == c:
                        // ++ and -- keep what was before: `x++ / 2`.
                        i += 2;
                        continue;

                    default:
                        _previous = Previous.Operator;
                        break;
                }

                i++;
            }
        }

        private void ScanTemplate(ref int i)
        {
            i++;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '\\')
                {
                    i += 2;
                }
                else if (c == '`')
                {
                    i++;
                    return;
                }
                else if (c == '$' && Peek(i + 1) == '{')
                {
                    i += 2;
                    _previous = Previous.Operator;
                    ScanCode(ref i, stopAtBrace: true);
                }
                else
                {
                    i++;
                }
            }
        }

        private bool IsJsxStart(int i)
        {
            var next = Peek(i + 1);

            if (next == '>')
            {
                return true;
            }

            if (!IsIdentifierStart(next))
            {
                return false;
            }

            // A generic arrow function in a .tsx file: <T,>(x: T) => x, <T extends U>(...).
            var end = JsxNameEnd(i + 1);
            var after = SkipSpaces(end);
            return Peek(after) != ',' && !text.AsSpan(after).StartsWith("extends ", StringComparison.Ordinal);
        }

        private void ScanJsxElement(ref int i)
        {
            // <
            i++;

            if (Peek(i) == '>')
            {
                // A fragment.
                i++;
                ScanJsxChildren(ref i);
                return;
            }

            ScanJsxName(ref i);

            while (i < text.Length)
            {
                i = SkipSpacesAndComments(i);
                var c = Peek(i);

                if (c == '/' && Peek(i + 1) == '>')
                {
                    i += 2;
                    return;
                }

                if (c == '>')
                {
                    i++;
                    ScanJsxChildren(ref i);
                    return;
                }

                if (c == '<')
                {
                    // Type arguments of the element: <Select<string> ...>.
                    var depth = 0;

                    do
                    {
                        if (text[i] == '<')
                        {
                            depth++;
                        }
                        else if (text[i] == '>')
                        {
                            depth--;
                        }
                        else if (IsIdentifierStart(text[i]))
                        {
                            var start = i;
                            i = IdentifierEnd(i);
                            Words.Add(new Word(start, i - start, false));
                            continue;
                        }

                        i++;
                    }
                    while (i < text.Length && depth > 0);

                    continue;
                }

                if (c == '{')
                {
                    // A spread attribute.
                    i++;
                    _previous = Previous.Operator;
                    ScanCode(ref i, stopAtBrace: true);
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    var start = i;
                    i = JsxNameEnd(i);
                    Words.Add(new Word(start, i - start, false));
                    i = SkipSpacesAndComments(i);

                    if (Peek(i) != '=')
                    {
                        continue;
                    }

                    i = SkipSpacesAndComments(i + 1);
                    var value = Peek(i);

                    if (value is '"' or '\'')
                    {
                        var end = text.IndexOf(value, i + 1);
                        i = end < 0 ? text.Length : end + 1;
                    }
                    else if (value == '{')
                    {
                        i++;
                        _previous = Previous.Operator;
                        ScanCode(ref i, stopAtBrace: true);
                    }
                    else if (value == '<')
                    {
                        ScanJsxElement(ref i);
                    }

                    continue;
                }

                // Anything else is invalid here: move on.
                i++;
            }
        }

        private void ScanJsxChildren(ref int i)
        {
            while (i < text.Length)
            {
                var c = text[i];

                if (c == '{')
                {
                    i++;
                    _previous = Previous.Operator;
                    ScanCode(ref i, stopAtBrace: true);
                }
                else if (c == '<' && Peek(i + 1) == '/')
                {
                    // The closing tag.
                    i = SkipSpacesAndComments(i + 2);

                    if (Peek(i) != '>')
                    {
                        ScanJsxName(ref i);
                    }

                    var end = text.IndexOf('>', i);
                    i = end < 0 ? text.Length : end + 1;
                    return;
                }
                else if (c == '<')
                {
                    ScanJsxElement(ref i);
                }
                else
                {
                    i++;
                }
            }
        }

        /// <summary>
        /// A tag name: identifiers joined by <c>.</c> (<c>Foo.Bar</c>) or <c>:</c>, with dashes (<c>my-element</c>).
        /// </summary>
        private void ScanJsxName(ref int i)
        {
            var afterDot = false;

            while (i < text.Length && IsIdentifierStart(text[i]))
            {
                var start = i;
                i = JsxNameEnd(i);
                Words.Add(new Word(start, i - start, afterDot));

                if (Peek(i) is '.' or ':')
                {
                    afterDot = text[i] == '.';
                    i++;
                    continue;
                }

                break;
            }
        }

        private int JsxNameEnd(int i)
        {
            while (i < text.Length && (IsIdentifierPart(text[i]) || text[i] == '-'))
            {
                i++;
            }

            return i;
        }

        private int SkipString(int i, char quote)
        {
            i++;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    return i + 1;
                }

                if (c == '\n')
                {
                    // Unterminated.
                    return i;
                }

                i++;
            }

            return i;
        }

        private int SkipRegex(int i)
        {
            var inClass = false;
            i++;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == '\n')
                {
                    return i;
                }

                if (c == '[')
                {
                    inClass = true;
                }
                else if (c == ']')
                {
                    inClass = false;
                }
                else if (c == '/' && !inClass)
                {
                    i++;

                    // Flags.
                    while (i < text.Length && char.IsAsciiLetter(text[i]))
                    {
                        i++;
                    }

                    return i;
                }

                i++;
            }

            return i;
        }

        private int NumberEnd(int i)
        {
            while (i < text.Length)
            {
                var c = text[i];

                if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
                {
                    i++;
                }
                else if (c is '+' or '-' && text[i - 1] is 'e' or 'E' && !text.AsSpan(0, i).Contains("0x", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            return i;
        }

        private int IdentifierEnd(int i)
        {
            while (i < text.Length)
            {
                if (text[i] == '\\' && Peek(i + 1) == 'u')
                {
                    // \uXXXX or \u{X...}
                    i += 2;

                    if (Peek(i) == '{')
                    {
                        var end = text.IndexOf('}', i);
                        i = end < 0 ? text.Length : end + 1;
                    }
                    else
                    {
                        i = Math.Min(i + 4, text.Length);
                    }

                    continue;
                }

                if (!IsIdentifierPart(text[i]))
                {
                    break;
                }

                i++;
            }

            return i;
        }

        private int SkipSpaces(int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return i;
        }

        private int SkipSpacesAndComments(int i)
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                else if (text[i] == '/' && Peek(i + 1) == '/')
                {
                    i = LineEnd(i);
                }
                else if (text[i] == '/' && Peek(i + 1) == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
                else
                {
                    break;
                }
            }

            return i;
        }

        private int LineEnd(int i)
        {
            var end = text.IndexOf('\n', i);
            return end < 0 ? text.Length : end;
        }

        private char Peek(int i) => i < text.Length ? text[i] : '\0';
    }

    private static bool IsIdentifierStart(char c)
    {
        if (char.IsAsciiLetter(c) || c is '$' or '_')
        {
            return true;
        }

        return c > 0x7f && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    private static bool IsIdentifierPart(char c)
    {
        if (char.IsAsciiLetterOrDigit(c) || c is '$' or '_' or '‌' or '‍')
        {
            return true;
        }

        return c > 0x7f && (IsIdentifierStart(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation);
    }
}
