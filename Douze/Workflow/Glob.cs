using System.Text;
using System.Text.RegularExpressions;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// A glob pattern over virtual paths.
/// <c>*</c> matches anything except <c>/</c>; <c>**</c> matches anything including <c>/</c>; <c>?</c> matches one
/// character except <c>/</c>; <c>{a,b}</c> alternatives (nested allowed); <c>[abc]</c>, <c>[a-z]</c>, <c>[!abc]</c>
/// character classes; <c>\</c> escapes the next character; a leading <c>/</c> is ignored; a leading <c>!</c> negates.
/// Matching is case sensitive and ordinal.
/// </summary>
internal sealed class Glob
{
    private readonly Regex _regex;

    // What every matching path (without its leading '/') starts and ends with: most paths are rejected without the
    // regular expression, which is interpreted under native AOT. 'prefix/**' matches the folder itself too, without
    // its slash.
    private readonly string _literalPrefix;
    private readonly string _literalSuffix;

    // For '**/*.md' and '**/*.{md,html}', the patterns of the built-in steps: the ends that decide the match alone.
    private readonly string[]? _endings;

    private Glob(string pattern, bool negated, Regex regex, string fixedPrefix, string body)
    {
        Pattern = pattern;
        Negated = negated;
        _regex = regex;
        FixedPrefix = fixedPrefix;
        _literalPrefix = fixedPrefix[1..].TrimEnd('/');
        _literalSuffix = GetLiteralSuffix(body);
        _endings = GetEndings(body);
    }

    public bool Negated { get; }

    /// <summary>
    /// The pattern as written, including any <c>!</c>.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// The literal directory prefix of the pattern, starting and ending with <c>/</c>.
    /// </summary>
    internal string FixedPrefix { get; }

    /// <exception cref="GlobFormatException">The pattern is invalid.</exception>
    public static Glob Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var body = pattern;
        var offset = 0;
        var negated = false;

        if (body.StartsWith('!'))
        {
            negated = true;
            body = body[1..];
            offset++;
        }

        if (body.StartsWith('/'))
        {
            body = body[1..];
            offset++;
        }

        if (body.Length == 0)
        {
            throw new GlobFormatException("empty pattern", offset);
        }

        var regex = new StringBuilder("^");
        new Translator(body, offset, regex).Translate();
        regex.Append('$');

        return new Glob(pattern, negated, new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline), GetFixedPrefix(body), body);
    }

    /// <summary>
    /// Whether <paramref name="path"/> matches the pattern. The leading <c>/</c> of the path is ignored.
    /// <see cref="Negated"/> is not applied here: it is interpreted by <see cref="GlobSet"/>.
    /// </summary>
    public bool IsMatch(FPath path)
    {
        var value = path.FullName;
        var span = value.StartsWith('/') ? value.AsSpan(1) : value.AsSpan();

        if (_endings != null)
        {
            foreach (var ending in _endings)
            {
                if (span.EndsWith(ending, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return span.StartsWith(_literalPrefix, StringComparison.Ordinal)
            && span.EndsWith(_literalSuffix, StringComparison.Ordinal)
            && _regex.IsMatch(span);
    }

    public override string ToString() => Pattern;

    private static string GetFixedPrefix(string body)
    {
        var wildcard = body.AsSpan().IndexOfAny("*?[{\\");
        var literal = wildcard < 0 ? body : body[..wildcard];
        var slash = literal.LastIndexOf('/');

        return slash < 0 ? "/" : "/" + literal[..(slash + 1)];
    }

    /// <summary>
    /// The literal end of the pattern, after its last wildcard, class or alternative (<c>.md</c> for
    /// <c>**/*.md</c>); empty when the pattern escapes a character, or ends with a wildcard.
    /// </summary>
    private static string GetLiteralSuffix(string body)
    {
        if (body.Contains('\\'))
        {
            return "";
        }

        var special = body.AsSpan().LastIndexOfAny("*?]}");
        var suffix = body[(special + 1)..];

        // The slash of '**/' is optional: '**/index.md' matches 'index.md'.
        return special >= 1 && body[special - 1] == '*' && body[special] == '*' && suffix.StartsWith('/') ? suffix[1..] : suffix;
    }

    /// <summary>
    /// The ends a path must have to match <c>**/*</c> followed by a literal without <c>/</c>, or by literals around one
    /// group of literal alternatives (<c>**/*.{md,html}</c>): any path ending with one of them matches, since
    /// <c>**/*</c> matches any path. Null for other patterns.
    /// </summary>
    private static string[]? GetEndings(string body)
    {
        if (!body.StartsWith("**/*", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = body[4..];

        if (rest.AsSpan().IndexOfAny("*?[]\\/") >= 0)
        {
            return null;
        }

        var open = rest.IndexOf('{');

        if (open < 0)
        {
            return rest.Contains('}') || rest.Contains(',') ? null : [rest];
        }

        var close = rest.IndexOf('}');

        if (close < open || rest.IndexOf('{', open + 1) >= 0 || rest.IndexOf('}', close + 1) >= 0)
        {
            return null;
        }

        var (head, tail) = (rest[..open], rest[(close + 1)..]);

        if (head.Contains(',') || tail.Contains(','))
        {
            return null;
        }

        return [.. rest[(open + 1)..close].Split(',').Select(x => head + x + tail)];
    }

    private ref struct Translator
    {
        private readonly string _pattern;
        private readonly int _offset;
        private readonly StringBuilder _regex;
        private int _position;
        private int _braceDepth;

        public Translator(string pattern, int offset, StringBuilder regex)
        {
            _pattern = pattern;
            _offset = offset;
            _regex = regex;
        }

        public void Translate()
        {
            while (_position < _pattern.Length)
            {
                var c = _pattern[_position];

                switch (c)
                {
                    case '*':
                        TranslateStar();
                        break;

                    case '?':
                        _regex.Append("[^/]");
                        _position++;
                        break;

                    case '[':
                        TranslateClass();
                        break;

                    case '{':
                        _braceDepth++;
                        _regex.Append("(?:");
                        _position++;
                        break;

                    case '}' when _braceDepth > 0:
                        _braceDepth--;
                        _regex.Append(')');
                        _position++;
                        break;

                    case ',' when _braceDepth > 0:
                        _regex.Append('|');
                        _position++;
                        break;

                    case '}':
                        throw new GlobFormatException("unmatched '}'", _offset + _position);

                    case '\\':
                        if (_position + 1 >= _pattern.Length)
                        {
                            throw new GlobFormatException("trailing escape character", _offset + _position);
                        }

                        _regex.Append(Regex.Escape(_pattern[_position + 1].ToString()));
                        _position += 2;
                        break;

                    default:
                        _regex.Append(Regex.Escape(c.ToString()));
                        _position++;
                        break;
                }
            }

            if (_braceDepth > 0)
            {
                throw new GlobFormatException("unterminated alternative", _offset + _pattern.Length);
            }
        }

        private void TranslateStar()
        {
            var isDouble = _position + 1 < _pattern.Length && _pattern[_position + 1] == '*';

            if (!isDouble)
            {
                _regex.Append("[^/]*");
                _position++;
                return;
            }

            var atSegmentStart = _position == 0 || _pattern[_position - 1] == '/';
            var end = _position + 2;
            var atSegmentEnd = end == _pattern.Length || _pattern[end] == '/';

            if (atSegmentStart && atSegmentEnd)
            {
                if (end == _pattern.Length)
                {
                    if (_position == 0)
                    {
                        // "**": anything
                        _regex.Append(".*");
                    }
                    else
                    {
                        // "prefix/**": the folder itself and anything below it
                        _regex.Length--; // removes the escaped '/'
                        _regex.Append("(?:/.*)?");
                    }

                    _position = end;
                }
                else
                {
                    // "**/" at the start or in the middle: zero or more directories
                    _regex.Append("(?:.*/)?");
                    _position = end + 1;
                }

                return;
            }

            // "**" inside a segment behaves like "*" across separators
            _regex.Append(".*");
            _position = end;
        }

        private void TranslateClass()
        {
            var start = _position;
            _position++;

            var negated = false;

            if (_position < _pattern.Length && (_pattern[_position] == '!' || _pattern[_position] == '^'))
            {
                negated = true;
                _position++;
            }

            var content = new StringBuilder();
            var first = true;

            while (_position < _pattern.Length && (_pattern[_position] != ']' || first))
            {
                var c = _pattern[_position];

                if (c == '\\' && _position + 1 < _pattern.Length)
                {
                    _position++;
                    c = _pattern[_position];
                }

                // '-' between two characters is a range, anything else is literal
                if (c == '-' && !first && _position + 1 < _pattern.Length && _pattern[_position + 1] != ']')
                {
                    content.Append('-');
                }
                else
                {
                    content.Append(c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c : c.ToString());
                }

                first = false;
                _position++;
            }

            if (_position >= _pattern.Length)
            {
                throw new GlobFormatException("unterminated character class", _offset + start);
            }

            _position++; // ']'

            _regex.Append(negated ? "[^/" : "[").Append(content).Append(']');
        }
    }
}

/// <summary>
/// An invalid glob pattern. <see cref="Position"/> is the offending character, in the pattern as written.
/// </summary>
internal sealed class GlobFormatException : FormatException
{
    public GlobFormatException(string message, int position) : base(message)
    {
        Position = position;
    }

    public int Position { get; }
}
