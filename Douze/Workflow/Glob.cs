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

    private Glob(string pattern, bool negated, Regex regex, string fixedPrefix)
    {
        Pattern = pattern;
        Negated = negated;
        _regex = regex;
        FixedPrefix = fixedPrefix;
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

        return new Glob(pattern, negated, new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline), GetFixedPrefix(body));
    }

    /// <summary>
    /// Whether <paramref name="path"/> matches the pattern. The leading <c>/</c> of the path is ignored.
    /// <see cref="Negated"/> is not applied here: it is interpreted by <see cref="GlobSet"/>.
    /// </summary>
    public bool IsMatch(FPath path)
    {
        var value = path.FullName;
        return _regex.IsMatch(value.StartsWith('/') ? value.AsSpan(1) : value.AsSpan());
    }

    public override string ToString() => Pattern;

    private static string GetFixedPrefix(string body)
    {
        var wildcard = body.AsSpan().IndexOfAny("*?[{\\");
        var literal = wildcard < 0 ? body : body[..wildcard];
        var slash = literal.LastIndexOf('/');

        return slash < 0 ? "/" : "/" + literal[..(slash + 1)];
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
