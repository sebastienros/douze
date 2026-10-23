using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Parser;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Douze.Highlighting;

/// <summary>
/// A run of characters of a line with one style per theme.
/// </summary>
internal readonly record struct Token(int Start, int End, TokenStyle[] Styles);

/// <summary>
/// Tokenizes code with TextMate grammars (TextMateSharp, on Oniguruma), and styles the tokens in every theme at once:
/// a line is tokenized once, each token's style is resolved from its scopes in each theme, and adjacent tokens with
/// the same styles merge. A language is tokenized by registries of its own (its grammar, the grammars it embeds and the
/// injections into them), rented from a pool: one grammar is not used by two threads at once.
/// </summary>
internal sealed partial class CodeTokenizer
{
    /// <summary>
    /// The longest a line is tokenized for by default (<see cref="CodeTokenizer(IEnumerable{GrammarInfo}, IReadOnlyList{CodeTheme}, TimeSpan?)"/>).
    /// </summary>
    internal static readonly TimeSpan DefaultLineTimeLimit = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> PlainLanguages = new(["", "plaintext", "txt", "text", "plain", "ansi"], StringComparer.Ordinal);

    private readonly Dictionary<string, GrammarInfo> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrammarInfo> _byScope = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<Registry>> _pools = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<CodeTheme> _themes;
    private readonly TimeSpan _lineTimeLimit;

    /// <param name="grammars">The grammars, the first one of a name or scope winning.</param>
    /// <param name="themes">The themes tokens are styled in.</param>
    /// <param name="lineTimeLimit">
    /// The longest a line is tokenized for (<see cref="DefaultLineTimeLimit"/>). A line that takes longer fails rather
    /// than keeping the style of where it stopped: where a line stops depends on the load of the machine (a registry
    /// compiles its rules on its first lines, while other units run), and a build must not.
    /// </param>
    public CodeTokenizer(IEnumerable<GrammarInfo> grammars, IReadOnlyList<CodeTheme> themes, TimeSpan? lineTimeLimit = null)
    {
        _themes = themes;
        _lineTimeLimit = lineTimeLimit ?? DefaultLineTimeLimit;

        foreach (var grammar in grammars)
        {
            _byScope.TryAdd(grammar.ScopeName, grammar);

            if (grammar.InjectTo.Count > 0)
            {
                continue;
            }

            _byName.TryAdd(grammar.Name, grammar);

            foreach (var alias in grammar.Aliases)
            {
                _byName.TryAdd(alias, grammar);
            }
        }
    }

    public IReadOnlyList<CodeTheme> Themes => _themes;

    /// <summary>
    /// Whether a fence language is plain text: no language, <c>text</c>, <c>txt</c>, <c>plain</c>, <c>plaintext</c>
    /// (and <c>ansi</c>, whose escape sequences are not interpreted).
    /// </summary>
    public static bool IsPlain(string language) => PlainLanguages.Contains(language);

    /// <summary>
    /// The grammar of a language name or alias, or null.
    /// </summary>
    public GrammarInfo? Find(string language) => _byName.GetValueOrDefault(language);

    /// <summary>
    /// The tokens of each line of <paramref name="code"/> (lines split on <c>\n</c>), in the language's grammar; one
    /// token per line in the themes' foregrounds for plain text or a language without grammar.
    /// </summary>
    public List<Token>[] Tokenize(string code, string language)
    {
        var lines = code.Split('\n');
        var result = new List<Token>[lines.Length];
        var grammar = IsPlain(language) ? null : Find(language);

        if (grammar == null)
        {
            var plain = _themes.Select(x => new TokenStyle(x.Foreground, 0)).ToArray();

            for (var i = 0; i < lines.Length; i++)
            {
                result[i] = lines[i].Length == 0 ? [] : [new Token(0, lines[i].Length, plain)];
            }

            return result;
        }

        var pool = _pools.GetOrAdd(grammar.Name, _ => []);

        if (!pool.TryTake(out var registry))
        {
            registry = new Registry(new Locator(this, grammar));
        }

        var reusable = false;

        try
        {
            var tmGrammar = registry.LoadGrammar(grammar.ScopeName)
                ?? throw new DouzeException($"the grammar of '{language}' ({grammar.ScopeName}) could not be loaded");
            IStateStack? state = null;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var tokens = new List<Token>();
                result[i] = tokens;

                if (line.Length == 0)
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                var tokenized = tmGrammar.TokenizeLine(line, state, _lineTimeLimit);

                // A line stops early only once past the limit (TextMateSharp does not expose its flag, and pads its
                // tokens to the end of the line).
                if (Stopwatch.GetElapsedTime(started) >= _lineTimeLimit)
                {
                    throw new DouzeException($"line {i + 1} of a '{language}' code block took more than {_lineTimeLimit.TotalSeconds:0.###} s to highlight");
                }

                state = tokenized.RuleStack;

                foreach (var token in tokenized.Tokens)
                {
                    var start = Math.Min(token.StartIndex, line.Length);
                    var end = Math.Min(token.EndIndex, line.Length);

                    if (end <= start)
                    {
                        continue;
                    }

                    var styles = new TokenStyle[_themes.Count];

                    for (var t = 0; t < styles.Length; t++)
                    {
                        styles[t] = _themes[t].Resolve(token.Scopes);
                    }

                    if (tokens.Count > 0 && tokens[^1].End == start && tokens[^1].Styles.AsSpan().SequenceEqual(styles))
                    {
                        tokens[^1] = tokens[^1] with { End = end };
                    }
                    else
                    {
                        tokens.Add(new Token(start, end, styles));
                    }
                }
            }

            reusable = true;
        }
        finally
        {
            // A registry that failed may be left half compiled.
            if (reusable)
            {
                pool.Add(registry);
            }
        }

        return result;
    }

    /// <summary>
    /// A grammar as TextMateSharp's rules (<see cref="Raw"/> objects, lists, strings, booleans and numbers), read from its
    /// JSON here: TextMateSharp's own reader leaves numbers and booleans out, so <c>applyEndPatternLast</c> is lost. What
    /// TextMateSharp cannot read and vscode-textmate tolerates is left out or fixed: captures that are not rules
    /// (<c>"captures": { "name": "..." }</c>) are ignored, and a <c>begin</c> rule without <c>end</c> or <c>while</c> ends
    /// at a character no code has (U+FFFF), as vscode-textmate does. A NUL in a pattern (<c>\x00</c>, <c>\x{0}</c>,
    /// <c>\u0000</c>) is U+0001: the Oniguruma build of TextMateSharp matches every character with a negated class that
    /// starts at NUL inside another class (<c>[a-z[^\x00-\x7F]]</c>, as CSS selectors are matched), and code has no NUL.
    /// </summary>
    internal static IRawGrammar ReadGrammar(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return (Raw)Convert(document.RootElement, null)!;

        static object? Convert(JsonElement element, string? key)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var raw = new Raw();
                    var captures = key is "captures" or "beginCaptures" or "endCaptures" or "whileCaptures";

                    foreach (var property in element.EnumerateObject())
                    {
                        if (captures && property.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (Convert(property.Value, property.Name) is { } value)
                        {
                            raw[property.Name] = value;
                        }
                    }

                    if (raw.ContainsKey("begin") && !raw.ContainsKey("end") && !raw.ContainsKey("while"))
                    {
                        raw["end"] = "\uFFFF";
                    }

                    return raw;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(x => Convert(x, null)).Where(x => x != null).ToList();
                case JsonValueKind.String:
                    var text = element.GetString()!;
                    return key is "begin" or "end" or "match" or "while" && NulEscape().IsMatch(text)
                        ? NulEscape().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value switch { "x00" => @"\x01", "u0000" => @"\u0001", _ => @"\x{1}" })
                        : text;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return key == "applyEndPatternLast" ? element.GetDouble() != 0 : element.TryGetInt32(out var number) ? number : element.GetDouble();
                default:
                    return null;
            }
        }
    }

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\(x00|x\{0+\}|u0000)")]
    private static partial Regex NulEscape();

    /// <summary>
    /// The grammars a registry of one language loads: the language's, the grammars of the scopes they include (any
    /// grammar known by its scope), and the injection grammars into those scopes that the language embeds.
    /// </summary>
    private sealed class Locator(CodeTokenizer tokenizer, GrammarInfo root) : IRegistryOptions
    {
        private readonly HashSet<string> _embedded = Embedded(tokenizer, root);

        private static HashSet<string> Embedded(CodeTokenizer tokenizer, GrammarInfo root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([root.Name]);

            while (pending.TryPop(out var name))
            {
                if (names.Add(name) && tokenizer._byScope.Values.FirstOrDefault(x => x.Name == name) is { } grammar)
                {
                    foreach (var embedded in grammar.Embedded)
                    {
                        pending.Push(embedded);
                    }
                }
            }

            return names;
        }

        public IRawGrammar? GetGrammar(string scopeName)
        {
            if (!tokenizer._byScope.TryGetValue(scopeName, out var grammar))
            {
                return null;
            }

            // A grammar of its own for each registry: TextMateSharp writes rule ids into it.
            return ReadGrammar(grammar.Load());
        }

        public ICollection<string>? GetInjections(string scopeName)
        {
            var injections = tokenizer._byScope.Values
                .Where(x => x.InjectTo.Contains(scopeName) && _embedded.Contains(x.Name))
                .Select(x => x.ScopeName)
                .ToList();
            return injections.Count == 0 ? null : injections;
        }

        public IRawTheme? GetTheme(string scopeName) => null;

        public IRawTheme GetDefaultTheme() => EmptyTheme.Instance;
    }

    /// <summary>
    /// The theme of the registries: styles are resolved by <see cref="CodeTheme"/>, not by the grammar.
    /// </summary>
    private sealed class EmptyTheme : IRawTheme
    {
        public static readonly EmptyTheme Instance = new();

        public string GetName() => "none";
        public string? GetInclude() => null;
        public ICollection<IRawThemeSetting> GetSettings() => [];
        public ICollection<IRawThemeSetting> GetTokenColors() => [];
        public ICollection<KeyValuePair<string, object>> GetGuiColors() => [];
    }
}
