using System.Collections.Concurrent;
using Tint;
using Tint.Grammars;

namespace Douze.Highlighting;

/// <summary>
/// A run of characters of a line with one style per theme.
/// </summary>
internal readonly record struct Token(int Start, int End, TokenStyle[] Styles);

/// <summary>
/// Tokenizes code with TextMate grammars (Tint, on GregsEx), and styles the tokens in every theme at once: a line is
/// tokenized once, each token's style is resolved from its scopes in each theme, and adjacent tokens with the same
/// styles merge. A language has a registry of its own (every grammar, and the injection grammars that apply to it),
/// whose compiled grammar its tokenizers share; a tokenizer is used by one thread at a time, rented from a pool.
/// </summary>
internal sealed class CodeTokenizer
{
    /// <summary>
    /// The longest a line is tokenized for by default (<see cref="CodeTokenizer(IEnumerable{CodeGrammar}, IReadOnlyList{CodeTheme}, TimeSpan?)"/>).
    /// </summary>
    internal static readonly TimeSpan DefaultLineTimeLimit = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> PlainLanguages = new(["", "plaintext", "txt", "text", "plain", "ansi"], StringComparer.Ordinal);

    private readonly Dictionary<string, string> _scopeByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeGrammar> _byScope = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _embeddedByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Language>> _languages = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<CodeTheme> _themes;
    private readonly TimeSpan _lineTimeLimit;

    /// <param name="grammars">The grammars, the first one of a name or scope winning.</param>
    /// <param name="themes">The themes tokens are styled in.</param>
    /// <param name="lineTimeLimit">
    /// The longest a line is tokenized for (<see cref="DefaultLineTimeLimit"/>). A line that takes longer fails rather
    /// than keeping the style of where it stopped: where a line stops depends on the load of the machine, and a build
    /// must not.
    /// </param>
    public CodeTokenizer(IEnumerable<CodeGrammar> grammars, IReadOnlyList<CodeTheme> themes, TimeSpan? lineTimeLimit = null)
    {
        _themes = themes;
        _lineTimeLimit = lineTimeLimit ?? DefaultLineTimeLimit;

        foreach (var grammar in grammars)
        {
            _byScope.TryAdd(grammar.ScopeName, grammar);

            // The languages a name embeds, whichever grammar of that name wins its scope.
            if (grammar.Embedded.Count > 0)
            {
                _embeddedByName.TryAdd(grammar.Name, [.. grammar.Embedded]);
            }

            if (grammar.InjectTo.Count > 0)
            {
                continue;
            }

            _scopeByName.TryAdd(grammar.Name, grammar.ScopeName);

            foreach (var alias in grammar.Aliases)
            {
                _scopeByName.TryAdd(alias, grammar.ScopeName);
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
    /// The grammar of a language name or alias, or null: the first grammar of the scope that name refers to.
    /// </summary>
    public CodeGrammar? Find(string language) =>
        _scopeByName.TryGetValue(language, out var scope) ? _byScope[scope] : null;

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

        var target = _languages.GetOrAdd(grammar.ScopeName, scope => new Lazy<Language>(() => CreateLanguage(scope, language))).Value;

        if (!target.Tokenizers.TryTake(out var rented))
        {
            rented = new RentedTokenizer(target.Registry.CreateTokenizer(grammar.ScopeName));
            rented.Tokenizer.TimeBudget = _lineTimeLimit;
        }

        var reusable = false;

        try
        {
            var state = StateStack.Initial;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var tokens = new List<Token>();
                result[i] = tokens;

                if (line.Length == 0)
                {
                    continue;
                }

                var tokenized = rented.Tokenizer.TokenizeLine(line, state, out state);

                if (rented.Tokenizer.StoppedEarly)
                {
                    throw new DouzeException($"line {i + 1} of a '{language}' code block took more than {_lineTimeLimit.TotalSeconds:0.###} s to highlight");
                }

                foreach (var token in tokenized)
                {
                    var start = Math.Min(token.Start, line.Length);
                    var end = Math.Min(token.End, line.Length);

                    if (end <= start)
                    {
                        continue;
                    }

                    var styles = rented.Styles(token.Scopes, _themes);

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
            // A tokenizer that failed is not reused.
            if (reusable)
            {
                target.Tokenizers.Add(rented);
            }
        }

        return result;
    }

    /// <summary>
    /// The registry of a language: every grammar (each scope's first, read when the language includes it), and the
    /// injection grammars that apply to it: Tint's, and those of tm-grammars into the languages it embeds.
    /// </summary>
    private Language CreateLanguage(string scope, string language)
    {
        var embedded = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([_byScope[scope].Name, language]);

        while (pending.TryPop(out var name))
        {
            if (embedded.Add(name) && _embeddedByName.TryGetValue(name, out var names))
            {
                foreach (var other in names)
                {
                    pending.Push(other);
                }
            }
        }

        var registry = new GrammarRegistry();

        foreach (var grammar in _byScope.Values)
        {
            if (grammar.InjectTo.Count == 0 || grammar.AlwaysInject || embedded.Contains(grammar.Name))
            {
                registry.Add(new LazySource(grammar));
            }
        }

        return new Language(registry);
    }

    private sealed class Language(GrammarRegistry registry)
    {
        public GrammarRegistry Registry { get; } = registry;

        public ConcurrentBag<RentedTokenizer> Tokenizers { get; } = [];
    }

    /// <summary>
    /// A tokenizer and the styles of the scope stacks it met: a stack is resolved in every theme once.
    /// </summary>
    private sealed class RentedTokenizer(Tokenizer tokenizer)
    {
        private readonly Dictionary<AttributedScopeStack, TokenStyle[]> _styles = [];

        public Tokenizer Tokenizer { get; } = tokenizer;

        public TokenStyle[] Styles(AttributedScopeStack scopes, IReadOnlyList<CodeTheme> themes)
        {
            if (!_styles.TryGetValue(scopes, out var styles))
            {
                var names = scopes.GetScopeNames();
                styles = new TokenStyle[themes.Count];

                for (var t = 0; t < styles.Length; t++)
                {
                    styles[t] = themes[t].Resolve(names);
                }

                _styles[scopes] = styles;
            }

            return styles;
        }
    }

    /// <summary>
    /// A grammar as a registry sees it: its scope and the scopes it injects into are known up front, the grammar is
    /// read when a tokenizer reaches it. The language id is left out: a registry holds every grammar, more than the 255
    /// languages Tint numbers.
    /// </summary>
    private sealed class LazySource(CodeGrammar grammar) : IGrammarSource
    {
        private GrammarInfo? _info;

        public string ScopeName => grammar.ScopeName;

        public GrammarInfo Info => _info ??= grammar.InjectTo.Count == 0
            ? GrammarInfo.Empty
            : new GrammarInfo { InjectTo = grammar.InjectTo, InjectionSelector = grammar.AlwaysInject ? grammar.Source().Info.InjectionSelector : null };

        public RawGrammar GetGrammar() => grammar.Source().GetGrammar();
    }
}
