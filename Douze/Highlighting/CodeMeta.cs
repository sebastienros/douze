using System.Text;
using System.Text.RegularExpressions;

namespace Douze.Highlighting;

internal enum MetaKind { String, Boolean, Range, Regex }

/// <summary>
/// An option of a fence's meta: <c>key=value</c>, <c>key="quoted"</c>, <c>key='quoted'</c>, <c>key=/regex/</c>,
/// <c>key={range}</c> or a bare word (a boolean, <c>key=false</c> to turn it off); the key is empty for a value alone
/// (<c>"text"</c>, <c>/regex/</c>, <c>{1-3}</c>).
/// </summary>
internal sealed record MetaOption(int Index, string Key, MetaKind Kind, string Value, bool Boolean = true);

/// <summary>
/// The options of a fence's meta, in the syntax of Markdown code fences that highlighters for the web share:
/// <c>title="Program.cs" {1-3} ins={4} "text" /regex/ showLineNumbers frame=terminal</c>. Keys compare case
/// insensitively; the last option of a key wins.
/// </summary>
internal sealed partial class CodeMeta
{
    public static readonly CodeMeta Empty = new("");

    public CodeMeta(string meta)
    {
        Raw = meta;
        Options = Parse(meta);
    }

    public string Raw { get; }

    public IReadOnlyList<MetaOption> Options { get; }

    /// <summary>
    /// The options of the keys (an empty key: values without a key), of a kind or any.
    /// </summary>
    public IEnumerable<MetaOption> List(IEnumerable<string> keys, MetaKind? kind = null)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        return Options.Where(x => set.Contains(x.Key) && (kind == null || x.Kind == kind));
    }

    public string? GetString(string key) => List([key], MetaKind.String).LastOrDefault()?.Value;

    public bool? GetBoolean(string key) => List([key], MetaKind.Boolean).LastOrDefault()?.Boolean;

    public int? GetInteger(string key)
    {
        var values = List([key]).Where(x => x.Kind is MetaKind.String or MetaKind.Range && IntegerPattern().IsMatch(x.Value.Trim())).ToList();
        return values.Count == 0 ? null : int.Parse(values[^1].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IEnumerable<string> GetRanges(string key) => List([key], MetaKind.Range).Select(x => x.Value);

    private static List<MetaOption> Parse(string input)
    {
        var options = new List<MetaOption>();
        var remaining = new StringBuilder(input);

        foreach (Match match in DelimitedValue().Matches(input))
        {
            // The groups of the delimiter that matched: key and value.
            var (key, value, delimiter) = match.Groups["dq"].Success ? (match.Groups["dqk"], match.Groups["dq"], '"')
                : match.Groups["sq"].Success ? (match.Groups["sqk"], match.Groups["sq"], '\'')
                : match.Groups["re"].Success ? (match.Groups["rek"], match.Groups["re"], '/')
                : (match.Groups["rgk"], match.Groups["rg"], '}');

            var unescaped = Regex.Replace(value.Value, @"\\(\\|" + Regex.Escape(delimiter.ToString()) + ")", "$1");
            var kind = delimiter switch { '/' => MetaKind.Regex, '}' => MetaKind.Range, _ => MetaKind.String };
            options.Add(new MetaOption(match.Index, key.Success ? key.Value : "", kind, unescaped));

            for (var i = match.Index; i < match.Index + match.Length; i++)
            {
                remaining[i] = ' ';
            }
        }

        foreach (Match match in SimpleOption().Matches(remaining.ToString()))
        {
            var key = match.Groups[1].Value;

            if (!match.Groups[2].Success || match.Groups[2].Value is "true" or "false")
            {
                options.Add(new MetaOption(match.Index, key, MetaKind.Boolean, "", match.Groups[2].Value != "false"));
            }
            else
            {
                options.Add(new MetaOption(match.Index, key, MetaKind.String, match.Groups[2].Value));
            }
        }

        options.Sort((a, b) => a.Index.CompareTo(b.Index));
        return options;
    }

    [GeneratedRegex("""
        (?:\s|^)(?:(?<dqk>[^\s"'/{=]+)\s*=\s*)?"(?<dq>(?:[^\\]|\\.)*?)"(?=\s|$)
        |(?:\s|^)(?:(?<sqk>[^\s"'/{=]+)\s*=\s*)?'(?<sq>(?:[^\\]|\\.)*?)'(?=\s|$)
        |(?:\s|^)(?:(?<rek>[^\s"'/{=]+)\s*=\s*)?/(?<re>(?:[^\\]|\\.)*?)/(?=\s|$)
        |(?:\s|^)(?:(?<rgk>[^\s"'/{=]+)\s*=\s*)?\{(?<rg>(?:[^\\]|\\.)*?)\}(?=\s|$)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DelimitedValue();

    [GeneratedRegex(@"([^\s=]+)(?:\s*=\s*(\S+))?")]
    private static partial Regex SimpleOption();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerPattern();
}
