using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Douze.Highlighting;

/// <summary>
/// The style of a token in one theme: its color (<c>#rrggbb</c> or <c>#rrggbbaa</c>, lower case) and its font style
/// (bits: 1 italic, 2 bold, 4 underline, 8 strikethrough).
/// </summary>
internal readonly record struct TokenStyle(string Color, int FontStyle)
{
    public bool Italic => (FontStyle & 1) != 0;
    public bool Bold => (FontStyle & 2) != 0;
    public bool Underline => (FontStyle & 4) != 0;
    public bool Strikethrough => (FontStyle & 8) != 0;
}

/// <summary>
/// A VS Code / TextMate color theme, normalized as code highlighters on the web do: its type (<c>dark</c> or
/// <c>light</c>, guessed from its editor colors when missing), its foreground and background (<c>editor.foreground</c>
/// and <c>editor.background</c>, else VS Code's defaults), and its token colors made readable on its background (a
/// minimum contrast ratio, <see cref="Color.EnsureContrast"/>). Resolves the style of a token from its scopes with
/// TextMate's rules: the rule trie, the specificity of selectors and their parent scopes (with the <c>&gt;</c> child
/// combinator), as vscode-textmate does.
/// </summary>
internal sealed partial class CodeTheme
{
    private const int NotSet = -1;

    private readonly TrieElement _root;
    private readonly string _defaultForeground;
    private readonly int _defaultFontStyle;
    private readonly ConcurrentDictionary<string, TokenStyle> _styles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<TrieRule>> _matches = new(StringComparer.Ordinal);

    private CodeTheme(string name, string type, string foreground, string background, IReadOnlyDictionary<string, string> colors, TrieElement root, string defaultForeground, int defaultFontStyle, string hash)
    {
        Name = name;
        Type = type;
        Foreground = foreground;
        Background = background;
        Colors = colors;
        _root = root;
        _defaultForeground = defaultForeground;
        _defaultFontStyle = defaultFontStyle;
        Hash = hash;
    }

    public string Name { get; }

    /// <summary>
    /// <c>dark</c> or <c>light</c>.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// The color of plain text, lower case hex.
    /// </summary>
    public string Foreground { get; }

    /// <summary>
    /// The background of code, lower case hex.
    /// </summary>
    public string Background { get; }

    /// <summary>
    /// The theme's workbench colors (<c>editor.background</c>, <c>editorLineNumber.foreground</c>...) as it defines them,
    /// <c>editor.foreground</c> and <c>editor.background</c> resolved.
    /// </summary>
    public IReadOnlyDictionary<string, string> Colors { get; }

    /// <summary>
    /// What the styles of the theme depend on: its type, colors and normalized rules.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    /// Reads a theme from its JSON (comments and trailing commas allowed).
    /// </summary>
    /// <param name="fallbackName">The name when the theme has none.</param>
    /// <param name="json">The theme.</param>
    /// <param name="minContrast">The minimum contrast ratio of token colors on the background, 0 to keep them.</param>
    public static CodeTheme Load(string fallbackName, byte[] json, double minContrast)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new DouzeException($"theme '{fallbackName}' is not a JSON object");

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root["colors"] is JsonObject colorObject)
        {
            foreach (var (key, value) in colorObject)
            {
                if (String(value) is { } text && text.Trim().Length > 0)
                {
                    colors[key] = text;
                }
            }
        }

        var type = String(root["type"]) is "dark" or "light" ? String(root["type"])! : null;

        if (type == null)
        {
            var background = Color.TryParse(colors.GetValueOrDefault("editor.background") ?? "#1e1e1e", out var b) ? b : Color.Black;
            var foreground = Color.TryParse(colors.GetValueOrDefault("editor.foreground") ?? "#bbbbbb", out var f) ? f : Color.White;
            type = background.Luminance < foreground.Luminance ? "dark" : "light";
        }

        var dark = type == "dark";
        var fg = Lower(String(root["fg"]) ?? colors.GetValueOrDefault("editor.foreground") ?? (dark ? "#bbbbbb" : "#333333"));
        var bg = Lower(String(root["bg"]) ?? colors.GetValueOrDefault("editor.background") ?? (dark ? "#1e1e1e" : "#ffffff"));
        colors["editor.foreground"] = fg;
        colors["editor.background"] = bg;

        // The rules: one per selector of a setting, scopes split on commas.
        var rules = new List<ParsedRule>();
        var settings = (root["settings"] ?? root["tokenColors"]) as JsonArray ?? [];

        for (var index = 0; index < settings.Count; index++)
        {
            if (settings[index] is not JsonObject { } entry || entry["settings"] is not JsonObject style)
            {
                continue;
            }

            string[] scopes = entry["scope"] switch
            {
                JsonValue value when String(value) is { } text => text.Trim(',').Split(','),
                JsonArray array => [.. array.Select(x => String(x) ?? "")],
                _ => [""],
            };

            var fontStyle = NotSet;

            if (String(style["fontStyle"]) is { } fontStyleText)
            {
                fontStyle = 0;

                foreach (var segment in fontStyleText.Split(' '))
                {
                    fontStyle |= segment switch { "italic" => 1, "bold" => 2, "underline" => 4, "strikethrough" => 8, _ => 0 };
                }
            }

            var ruleForeground = String(style["foreground"]) is { } color && HexColor().IsMatch(color) ? Lower(color) : null;

            foreach (var selector in scopes)
            {
                var segments = selector.Trim().Split(' ');
                var parents = segments.Length > 1 ? segments[..^1].Reverse().ToArray() : null;
                rules.Add(new ParsedRule(segments[^1], parents, index, fontStyle, ruleForeground));
            }
        }

        // A theme without a global rule gets one with its colors.
        if (settings.Count == 0 || settings[0] is not JsonObject first || first["scope"] != null)
        {
            rules.Insert(0, new ParsedRule("", null, -1, NotSet, fg));
        }

        if (minContrast > 0 && Color.TryParse(bg, out var codeBackground))
        {
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);

            string Fix(string color)
            {
                if (!cache.TryGetValue(color, out var fixedColor))
                {
                    fixedColor = Color.TryParse(color, out var parsed) ? Color.EnsureContrast(parsed, codeBackground, minContrast) : color;
                    cache[color] = fixedColor;
                }

                return fixedColor;
            }

            fg = Fix(fg);

            for (var i = 0; i < rules.Count; i++)
            {
                if (rules[i].Foreground is { } color)
                {
                    rules[i] = rules[i] with { Foreground = Fix(color) };
                }
            }
        }

        // vscode-textmate's resolveParsedThemeRules: sorted by scope, parent scopes then order; the global rules give
        // the defaults; the others go in the trie.
        rules.Sort(static (a, b) =>
        {
            var r = string.CompareOrdinal(a.Scope, b.Scope);

            if (r == 0)
            {
                r = CompareParents(a.Parents, b.Parents);
            }

            return r != 0 ? r : a.Index.CompareTo(b.Index);
        });

        var defaultFontStyle = 0;
        var defaultForeground = "#000000";
        var next = 0;

        for (; next < rules.Count && rules[next].Scope.Length == 0; next++)
        {
            if (rules[next].FontStyle != NotSet)
            {
                defaultFontStyle = rules[next].FontStyle;
            }

            if (rules[next].Foreground is { } color)
            {
                defaultForeground = color;
            }
        }

        var trie = new TrieElement(new TrieRule(0, null, NotSet, null), []);

        foreach (var rule in rules.Skip(next))
        {
            trie.Insert(0, rule.Scope, rule.Parents, rule.FontStyle, rule.Foreground);
        }

        var hash = string.Join('\n', [type, fg, bg, defaultForeground, defaultFontStyle.ToString(), .. rules.Select(x => $"{x.Scope}\0{string.Join(' ', x.Parents ?? [])}\0{x.FontStyle}\0{x.Foreground}")]);
        return new CodeTheme(String(root["name"]) ?? fallbackName, type, fg, bg, colors, trie, defaultForeground, defaultFontStyle, hash);
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Lower(string color) => color.StartsWith('#') ? color.ToLowerInvariant() : color;

    private static int CompareParents(string[]? a, string[]? b)
    {
        if (a == null || b == null)
        {
            return a == null ? (b == null ? 0 : -1) : 1;
        }

        if (a.Length != b.Length)
        {
            return a.Length - b.Length;
        }

        for (var i = 0; i < a.Length; i++)
        {
            var r = string.CompareOrdinal(a[i], b[i]);

            if (r != 0)
            {
                return r;
            }
        }

        return 0;
    }

    /// <summary>
    /// The style of a token whose scopes (outermost first) are <paramref name="scopes"/>: from the defaults, each scope's
    /// most specific rule whose parent scopes match the scopes around it sets the color and font style it defines.
    /// </summary>
    public TokenStyle Resolve(IList<string> scopes)
    {
        var key = string.Join(' ', scopes);

        if (_styles.TryGetValue(key, out var style))
        {
            return style;
        }

        var foreground = _defaultForeground;
        var fontStyle = _defaultFontStyle;

        for (var i = 0; i < scopes.Count; i++)
        {
            var candidates = _matches.GetOrAdd(scopes[i], _root.Match);

            foreach (var rule in candidates)
            {
                if (!MatchesParents(scopes, i, rule.Parents))
                {
                    continue;
                }

                if (rule.FontStyle != NotSet)
                {
                    fontStyle = rule.FontStyle;
                }

                if (rule.Foreground != null)
                {
                    foreground = rule.Foreground;
                }

                break;
            }
        }

        style = new TokenStyle(foreground, fontStyle);
        _styles.TryAdd(key, style);
        return style;
    }

    /// <summary>
    /// Whether the scopes outside <c>scopes[index]</c> match a rule's parent scopes (innermost first): each in turn,
    /// further out, skipping scopes, except after <c>&gt;</c> where the next scope must match.
    /// </summary>
    private static bool MatchesParents(IList<string> scopes, int index, string[]? parents)
    {
        if (parents is not { Length: > 0 })
        {
            return true;
        }

        var position = index - 1;

        for (var p = 0; p < parents.Length; p++)
        {
            var pattern = parents[p];
            var mustMatch = false;

            if (pattern == ">")
            {
                if (p == parents.Length - 1)
                {
                    return false;
                }

                pattern = parents[++p];
                mustMatch = true;
            }

            while (position >= 0 && !MatchesScope(scopes[position], pattern))
            {
                if (mustMatch)
                {
                    return false;
                }

                position--;
            }

            if (position < 0)
            {
                return false;
            }

            position--;
        }

        return true;
    }

    private static bool MatchesScope(string scope, string selector) =>
        scope.Length == selector.Length
            ? string.Equals(scope, selector, StringComparison.Ordinal)
            : scope.Length > selector.Length && scope[selector.Length] == '.' && string.CompareOrdinal(scope, 0, selector, 0, selector.Length) == 0;

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    private sealed record ParsedRule(string Scope, string[]? Parents, int Index, int FontStyle, string? Foreground);

    private sealed class TrieRule(int scopeDepth, string[]? parents, int fontStyle, string? foreground)
    {
        public int ScopeDepth { get; private set; } = scopeDepth;
        public string[]? Parents { get; } = parents;
        public int FontStyle { get; private set; } = fontStyle;
        public string? Foreground { get; private set; } = foreground;

        public TrieRule Clone() => new(ScopeDepth, Parents, FontStyle, Foreground);

        public void AcceptOverwrite(int scopeDepth, int fontStyle, string? foreground)
        {
            ScopeDepth = Math.Max(ScopeDepth, scopeDepth);

            if (fontStyle != NotSet)
            {
                FontStyle = fontStyle;
            }

            if (foreground != null)
            {
                Foreground = foreground;
            }
        }
    }

    /// <summary>
    /// A node of the rule trie: the rule of its scope prefix, and the rules with parent scopes.
    /// </summary>
    private sealed class TrieElement(TrieRule main, List<TrieRule> withParents)
    {
        private readonly Dictionary<string, TrieElement> _children = new(StringComparer.Ordinal);

        public List<TrieRule> Match(string scope)
        {
            if (scope.Length > 0)
            {
                var dot = scope.IndexOf('.');
                var head = dot < 0 ? scope : scope[..dot];

                if (_children.TryGetValue(head, out var child))
                {
                    return child.Match(dot < 0 ? "" : scope[(dot + 1)..]);
                }
            }

            // Stable: equally specific rules keep their order.
            return [.. withParents.Append(main).Order(Comparer<TrieRule>.Create(CompareSpecificity))];
        }

        public void Insert(int scopeDepth, string scope, string[]? parents, int fontStyle, string? foreground)
        {
            if (scope.Length == 0)
            {
                InsertHere(scopeDepth, parents, fontStyle, foreground);
                return;
            }

            var dot = scope.IndexOf('.');
            var head = dot < 0 ? scope : scope[..dot];

            if (!_children.TryGetValue(head, out var child))
            {
                child = new TrieElement(main.Clone(), [.. withParents.Select(x => x.Clone())]);
                _children[head] = child;
            }

            child.Insert(scopeDepth + 1, dot < 0 ? "" : scope[(dot + 1)..], parents, fontStyle, foreground);
        }

        private void InsertHere(int scopeDepth, string[]? parents, int fontStyle, string? foreground)
        {
            if (parents == null)
            {
                main.AcceptOverwrite(scopeDepth, fontStyle, foreground);
                return;
            }

            foreach (var rule in withParents)
            {
                if (CompareParents(rule.Parents, parents) == 0)
                {
                    rule.AcceptOverwrite(scopeDepth, fontStyle, foreground);
                    return;
                }
            }

            withParents.Add(new TrieRule(scopeDepth, parents, fontStyle == NotSet ? main.FontStyle : fontStyle, foreground ?? main.Foreground));
        }

        private static int CompareSpecificity(TrieRule a, TrieRule b)
        {
            if (a.ScopeDepth != b.ScopeDepth)
            {
                return b.ScopeDepth - a.ScopeDepth;
            }

            var aParents = a.Parents ?? [];
            var bParents = b.Parents ?? [];
            int ai = 0, bi = 0;

            while (true)
            {
                if (ai < aParents.Length && aParents[ai] == ">")
                {
                    ai++;
                }

                if (bi < bParents.Length && bParents[bi] == ">")
                {
                    bi++;
                }

                if (ai >= aParents.Length || bi >= bParents.Length)
                {
                    break;
                }

                var difference = bParents[bi].Length - aParents[ai].Length;

                if (difference != 0)
                {
                    return difference;
                }

                ai++;
                bi++;
            }

            return bParents.Length - aParents.Length;
        }
    }
}
