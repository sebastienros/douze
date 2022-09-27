using System.Text.RegularExpressions;
using Douze.FileSystems;

namespace Douze.Configuration;

internal sealed class IncludeRule
{
    private bool _matchFile = true;
    private bool _matchDir = true;
    private string _pattern = "";
    private Regex? _regex;
    private bool _negate = false;

    public bool Match(FPath path)
    {
        path = path.ToAbsolute();

        if (_regex == null)
        {
            return false;
        }

        if (!_matchDir && path.IsDirectory)
        {
            return false;
        }

        if (!_matchFile && !path.IsDirectory)
        {
            return false;
        }

        return _regex.IsMatch(path.FullName);
    }

    private IncludeRule()
    {
    }

    public static Predicate<FPath> ParseRules(params string[] patterns)
    {
        var rules = new List<IncludeRule>();

        foreach (var pattern in patterns)
        {
            if (pattern.TrimStart().StartsWith('#'))
            {
                continue;
            }

            // A blank line matches no files, so it can serve as a separator for readability.
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (TryParse(pattern, out var rule) && rule != null)
            {
                rules.Add(rule);
            }
        }

        if (!rules.Any())
        {
            return _ => false;
        }

        return (FPath path) =>
        {
            var ignore = false;

            foreach (var rule in rules)
            {
                if (rule.Match(path.FullName))
                {
                    ignore = true;

                    if (rule._negate)
                    {
                        ignore = false;
                    }
                }
            }

            if (!ignore)
            {
                return false;
            }

            return true;
        };
    }

    public static bool TryParse(string pattern, out IncludeRule? rule)
    {
        // Work with / as the directory separator
        pattern = pattern.Replace('\\', '/');

        // A line starting with # serves as a comment. Put a backslash ("\") in front of the first hash for patterns that begin with a hash.
        pattern = pattern.Replace("\\#", "#");

        rule = new IncludeRule();

        if (string.IsNullOrEmpty(pattern))
        {
            throw new ArgumentException("Invalid empty rule");
        }

        var firstChar = pattern[0];

        if (firstChar == '!')
        {
            rule._negate = true;

            if (pattern.Length > 1)
            {
                pattern = pattern[1..];
            }
            else
            {
                return false;
            }
        }

        // Escape dots for regex
        pattern = pattern.Replace(".", "\\.");

        // A leading slash matches the beginning of the pathname. For example, "/*.c" matches "cat-file.c" but not "mozilla-sha1/sha1.c".
        if (pattern.StartsWith("/"))
        {
            pattern = "^" + pattern;
        }
        else
        {
            // (/|^) means either "/" or start-of-line
            pattern = "(/|^)" + pattern;
        }

        if (pattern.EndsWith('/'))
        {
            rule._matchFile = false;
        }
        else
        {
            // match the whole word, either before / or the end of the string
            // i.e., "foo" matches "/foo", "bar/foo" but not "foobar"
            pattern += "(/|$)";
        }

        // A trailing "/**" matches everything inside. For example, "abc/**" matches all files inside directory "abc", with infinite depth.
        if (pattern.EndsWith("/**"))
        {
            rule._matchDir = false;
        }

        // A double asterisk matches zero or more directories. 
        // The pattern is to look for any chars, including /
        const string DoubleStartPlaceHolder = "$$DSPH$$";
        pattern = pattern.Replace("**", DoubleStartPlaceHolder);

        // "*" matches anything except "/"
        pattern = pattern.Replace("*", @"[^/]*");

        // "?" matches any one character except "/"
        pattern = pattern.Replace("?", @"[^/]");

        pattern = pattern.Replace(DoubleStartPlaceHolder, ".*");

        rule._pattern = pattern;

        rule._regex = new Regex(rule._pattern, RegexOptions.Compiled);

        return true;
    }

    public override string ToString()
    {
        return _pattern;
    }
}
