using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Douze.FileSystems;
using Douze.Highlighting;
using Douze.Liquid;
using Douze.Nodes;
using Douze.Progress;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: highlight</c>. Highlights the code fences of HTML pages (<c>&lt;pre data-lang="..." data-meta="..."&gt;</c>
/// as the <c>markdown</c> step writes them) with TextMate grammars, in one or more themes at once, and renders each
/// block with a Liquid template: its frame, title, lines, markers, collapsed sections, line numbers and the copy button,
/// and the twoslash popups of the fences that a code hover step annotated (<c>data-twoslash</c>).
/// </summary>
/// <remarks>
/// <para>The grammars of Tint (<see cref="CodeGrammar.Tint"/>: VS Code's, some compiled to regex kernels), then the
/// grammars and themes of the tm-grammars and tm-themes packages are bundled (<see cref="TextMateBundle"/>);
/// <c>grammars</c> and <c>themes</c> add files of the source folder, which win over the bundled ones. A token's style in each theme is a CSS variable of
/// the theme's index (<c>--0</c>, <c>--1</c>...), so that a stylesheet switches themes.</para>
/// <para>Options: <c>themes</c> (bundled names or paths of theme files; <c>github-dark</c> and <c>github-light</c> by
/// default), <c>min-contrast</c> (5.5: token colors are made readable on the background, 0 keeps the theme's),
/// <c>grammars</c> (names to grammar files), <c>aliases</c> (fence languages to grammar names), <c>templates</c>
/// (<c>block</c> and <c>popup</c>, in the includes folder; Douze's markup by default), <c>markers</c> (the background
/// of <c>mark</c>, <c>ins</c> and <c>del</c> in each theme, which the text they mark is made readable on),
/// <c>title-from-comment</c> (true), <c>copy-terminal-comments</c> (false: the code to copy of a terminal leaves its
/// comment lines out), <c>line-numbers</c> (false), <c>tab-width</c> (0: tabs are kept) and <c>stylesheet</c> (a path
/// of the output: a stylesheet of the themes for the default markup).</para>
/// <para>The tokens of each fence are memoized in the disk cache by its language, code and the step's settings.</para>
/// </remarks>
internal sealed partial class HighlightStep : Step, IMapStep
{
    private const string Version = "1";

    // The grammars of Tint.Grammars, which the tokens depend on.
    private static readonly string TintVersion = $"tint@{typeof(Tint.Grammars.TintGrammars).Assembly.GetName().Version}";

    private static readonly GlobSet HtmlFiles = GlobSet.Parse(["**/*.html"]);

    private static readonly string[] DefaultThemes = ["github-dark", "github-light"];

    private readonly WithOptions _options;
    private readonly IReadOnlyList<string> _themeNames;
    private readonly double _minContrast;
    private readonly FPath? _blockTemplate;
    private readonly FPath? _popupTemplate;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FPath> _grammarFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _markerColors = new(StringComparer.Ordinal);
    private readonly bool _titleFromComment;
    private readonly bool _copyTerminalComments;
    private readonly bool _lineNumbers;
    private readonly int _tabWidth;
    private readonly FPath? _stylesheet;

    private BuildContext? _build;
    private CodeTokenizer? _tokenizer;
    private CodeBlockSettings? _settings;
    private ParsedTemplate? _defaultBlock;
    private ParsedTemplate? _defaultPopup;
    private string? _stylesheetText;
    private Markdown.MarkdownRenderer? _docsMarkdown;
    private Hash128 _settingsKey;
    private readonly ConcurrentDictionary<string, int> _unknownLanguages = new(StringComparer.Ordinal);

    public HighlightStep(StepDefinition definition) : base(definition)
    {
        _options = new WithOptions(definition, "themes", "min-contrast", "grammars", "aliases", "templates", "markers", "title-from-comment", "copy-terminal-comments", "line-numbers", "tab-width", "stylesheet");
        _themeNames = _options.Has("themes") ? _options.GetStrings("themes") : DefaultThemes;
        _minContrast = definition.With["min-contrast"] switch
        {
            null => 5.5,
            NodeValue<int> { Value: >= 0 } i => i.Value,
            NodeValue<long> { Value: >= 0 } l => l.Value,
            NodeValue<decimal> { Value: >= 0 } m => (double)m.Value,
            NodeValue<double> { Value: >= 0 } d => d.Value,
            _ => throw Error("option 'min-contrast' must be a number, 0 or more"),
        };

        if (_themeNames.Count == 0)
        {
            throw Error("option 'themes' needs at least one theme");
        }

        foreach (var (key, value) in Mapping("aliases"))
        {
            _aliases[key] = value;
        }

        foreach (var (key, value) in Mapping("grammars"))
        {
            _grammarFiles[key] = new FPath(value).ToRelative();
        }

        foreach (var (key, value) in Mapping("templates"))
        {
            switch (key)
            {
                case "block":
                    _blockTemplate = new FPath(value);
                    break;
                case "popup":
                    _popupTemplate = new FPath(value);
                    break;
                default:
                    throw Error($"option 'templates' of step '{Name}' has an unknown template '{key}': block or popup");
            }
        }

        if (definition.With["markers"] is { } markers)
        {
            if (markers is not NodeObject markerObject)
            {
                throw Error($"option 'markers' of step '{Name}' must map mark, ins and del to a color per theme");
            }

            foreach (var (key, value) in markerObject)
            {
                string[] colors = value switch
                {
                    NodeValue<string> s => [s.Value],
                    NodeArray array when array.All(x => x is NodeValue<string>) => [.. array.Select(x => ((NodeValue<string>)x!).Value)],
                    _ => [],
                };

                if (key is not ("mark" or "ins" or "del") || colors.Length != _themeNames.Count || colors.Any(x => !Color.TryParse(x, out _)))
                {
                    throw Error($"option 'markers' of step '{Name}': '{key}' must be mark, ins or del, with a color per theme ({_themeNames.Count})");
                }

                _markerColors[key] = colors;
            }
        }

        _titleFromComment = _options.GetBool("title-from-comment", true);
        _copyTerminalComments = _options.GetBool("copy-terminal-comments", false);
        _lineNumbers = _options.GetBool("line-numbers");
        _tabWidth = _options.GetInt("tab-width");
        _stylesheet = _options.GetString("stylesheet") is { } stylesheet ? new FPath(stylesheet).ToRelative() : (FPath?)null;

        if (_tabWidth is < 0 or > 16)
        {
            throw Error($"option 'tab-width' of step '{Name}' must be 0 to 16");
        }
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => HtmlFiles;

    public override Hash128 CacheVersion => _settingsKey;

    private WorkflowValidationException Error(string message) => new(message, Definition.Location);

    private IEnumerable<KeyValuePair<string, string>> Mapping(string option)
    {
        if (Definition.With[option] is not { } node)
        {
            return [];
        }

        if (node is not NodeObject obj || obj.Any(x => x.Value is not NodeValue<string>))
        {
            throw Error($"option '{option}' of step '{Name}' must be a mapping of names to strings");
        }

        return obj.Select(x => KeyValuePair.Create(x.Key, ((NodeValue<string>)x.Value!).Value)).ToList();
    }

    public override async ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;
        _unknownLanguages.Clear();
        var bundle = TextMateBundle.Default;
        var keyParts = new List<string> { Version, bundle.Version, _minContrast.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        // Themes: bundled names, or files of the source folder.
        var themes = new List<CodeTheme>();

        foreach (var name in _themeNames)
        {
            byte[]? json = bundle.GetTheme(name);

            if (json == null && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.Contains('/')))
            {
                json = await ReadSourceAsync(context, new FPath(name).ToRelative(), "theme");
            }

            if (json == null)
            {
                throw new DouzeException($"step '{Name}': unknown theme '{name}'; the bundled themes are {string.Join(", ", bundle.Themes.Select(x => x.Name))}, or give the path of a theme file");
            }

            var theme = CodeTheme.Load(Path.GetFileNameWithoutExtension(name), json, _minContrast);
            themes.Add(theme);
            keyParts.Add(theme.Hash);
        }

        // Grammars: the site's first, so that they win over bundled ones of the same name or scope, then Tint's (the
        // kernel compiled ones of this build, then Tint.Grammars), then those of tm-grammars.
        var grammars = new List<CodeGrammar>();

        foreach (var (name, path) in _grammarFiles)
        {
            var json = await ReadSourceAsync(context, path, "grammar")
                ?? throw new DouzeException($"step '{Name}': the grammar file '{path}' of '{name}' does not exist");
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var scope = document.RootElement.TryGetProperty("scopeName", out var scopeName) ? scopeName.GetString() : null;

            if (scope == null)
            {
                throw new DouzeException($"step '{Name}': the grammar file '{path}' has no scopeName");
            }

            var source = new Lazy<Tint.Grammars.IGrammarSource>(() => CodeGrammar.Load(json, []));
            grammars.Add(new CodeGrammar(name, scope, [], [], [], () => source.Value));
            keyParts.Add($"{name}\0{scope}\0{Hash128.Of(json)}");
        }

        grammars.AddRange(CodeGrammar.Tint);
        grammars.AddRange(bundle.Grammars);
        keyParts.Add(TintVersion);
        _tokenizer = new CodeTokenizer(grammars, themes);

        foreach (var (alias, target) in _aliases.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            keyParts.Add($"alias\0{alias}\0{target}");
        }

        var markers = CodeBlockSettings.DefaultMarkerBackgrounds(themes);

        foreach (var (type, colors) in _markerColors)
        {
            markers[type] = [.. colors.Select(Color.Parse)];
        }

        _settings = new CodeBlockSettings
        {
            Themes = themes,
            MarkerBackgrounds = markers,
            MinContrast = _minContrast,
            TitleFromComment = _titleFromComment,
            CopyTerminalWithoutComments = !_copyTerminalComments,
            LineNumbers = _lineNumbers,
        };

        keyParts.AddRange(markers.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}\0{string.Join(',', x.Value.Select(c => c.ToHex()))}"));
        keyParts.Add($"{_titleFromComment}\0{_copyTerminalComments}\0{_lineNumbers}\0{_tabWidth}\0{_stylesheet}");

        // Documentation comments are GitHub Flavored Markdown, their HTML shown as text.
        _docsMarkdown = context.Markdown.WithExtensions(["gfm-pipetables", "strikethrough", "autolinks", "tasklists", "footnotes", "nohtml"]);

        _defaultBlock = ParseDefault(context, "block.liquid");
        _defaultPopup = ParseDefault(context, "popup.liquid");
        keyParts.Add(_defaultBlock.Hash.ToString());
        keyParts.Add(_defaultPopup.Hash.ToString());

        if (_stylesheet != null)
        {
            _stylesheetText = CodeStylesheet.Write(themes);
        }

        _settingsKey = Hash128.OfStrings(keyParts);
    }

    public override ValueTask DisposeAsync()
    {
        // The languages without grammar, rendered as plain text, once per build.
        if (_build != null && !_unknownLanguages.IsEmpty)
        {
            var languages = string.Join(", ", _unknownLanguages.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"'{x.Key}' ({x.Value} {(x.Value == 1 ? "fence" : "fences")})"));
            _build.View.Message(LogLevel.Warning, Name, $"no grammar for {languages}: rendered as plain text; add one with the option 'grammars' or map the language with 'aliases'");
        }

        return default;
    }

    private static ParsedTemplate ParseDefault(BuildContext context, string name)
    {
        using var stream = typeof(HighlightStep).Assembly.GetManifestResourceStream($"highlight/{name}")
            ?? throw new InvalidOperationException($"the resource highlight/{name} is missing");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return context.Templates.GetOrParse(Hash128.OfText(text), text, $"(douze)/highlight/{name}");
    }

    private async ValueTask<byte[]?> ReadSourceAsync(BuildContext context, FPath path, string kind)
    {
        try
        {
            await using var stream = await context.Source.OpenFileAsync(FPath.Combine(FPath.Root, path));
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DouzeException($"step '{Name}': the {kind} file '{path}' cannot be read: {e.Message}");
        }
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!file.Content.IsText)
        {
            return FileList.Of(file);
        }

        var html = await file.Content.ReadTextAsync(ct);

        if (!html.Contains("<pre", StringComparison.Ordinal))
        {
            return FileList.Of(file);
        }

        var fences = FindFences(html);

        if (fences.Count == 0)
        {
            return FileList.Of(file);
        }

        var renderer = await CreateRendererAsync(file, context, ct);
        var builder = new StringBuilder(html.Length * 2);
        var last = 0;

        foreach (var fence in fences)
        {
            builder.Append(html, last, fence.Start - last);
            builder.Append(await renderer.RenderFenceAsync(fence.Language, fence.Meta, fence.Code, fence.Twoslash, popup: false));
            last = fence.End;
        }

        builder.Append(html, last, html.Length - last);
        context.Log(LogLevel.Debug, $"{file.SourcePath ?? file.Path}: {fences.Count} fences, {renderer.Tokenized} tokenized, {renderer.Memoized} from the cache");

        if (_stylesheet is { } stylesheet)
        {
            context.AddOutput(new VirtualFile(FPath.Combine(FPath.Root, stylesheet), FileContent.FromText(_stylesheetText!), Metadata.Empty, "text/css", Provenance.Source));
        }

        return FileList.Of(file.With(Name, content: FileContent.FromText(builder.ToString())));
    }

    private async ValueTask<FenceRenderer> CreateRendererAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        async ValueTask<ParsedTemplate> LoadAsync(FPath? path, ParsedTemplate fallback)
        {
            if (path is not { } p)
            {
                return fallback;
            }

            var loaded = await context.Build.Templates.LoadAsync(p, context.Dependencies, ct)
                ?? throw new DouzeException($"step '{Name}': the template '{p}' does not exist in the includes folder");
            return loaded.Template;
        }

        var block = await LoadAsync(_blockTemplate, _defaultBlock!);
        var popup = await LoadAsync(_popupTemplate, _defaultPopup!);
        var templateContext = context.CreateTemplateContext(new MetadataValue(file.Metadata), file, ct);
        return new FenceRenderer(this, context, templateContext, block.Template, popup.Template);
    }

    /// <summary>
    /// Renders the fences of one page: highlights them, renders their popups and their blocks with the templates.
    /// </summary>
    private sealed class FenceRenderer(HighlightStep step, StepContext context, TemplateContext templateContext, IFluidTemplate block, IFluidTemplate popup)
    {
        /// <summary>
        /// The code (fences, popup types) tokenized by this page, and taken from the cache.
        /// </summary>
        public int Tokenized { get; private set; }

        public int Memoized { get; private set; }

        public async ValueTask<string> RenderFenceAsync(string language, string meta, string code, string? twoslash, bool popup)
        {
            if (step._tabWidth > 0)
            {
                code = code.Replace("\t", new string(' ', step._tabWidth), StringComparison.Ordinal);
            }

            var builder = new CodeBlockBuilder(step._settings!, TokenizeAsync, RenderPopupAsync, RenderTypeAsync, RenderDocsAsync);
            var model = await builder.BuildAsync(code, language, meta, twoslash, popup);
            templateContext.SetValue("code", ToValue(model));

            // A template file's last line break is not the block's.
            return (await block.RenderAsync(templateContext, OutputEncoders.Html)).TrimEnd('\r', '\n');
        }

        private ValueTask<string> RenderTypeAsync(string code, string language) => RenderFenceAsync(language, "", code, null, popup: true);

        private async ValueTask<string> RenderDocsAsync(string markdown)
        {
            var html = (await step._docsMarkdown!.RenderAsync(markdown, null, context.Dependencies)).TrimEnd();

            // The fences of the documentation, highlighted as types are.
            var fences = step.FindFences(html);

            if (fences.Count == 0)
            {
                return html;
            }

            var builder = new StringBuilder(html.Length * 2);
            var last = 0;

            foreach (var fence in fences)
            {
                builder.Append(html, last, fence.Start - last);
                builder.Append(await RenderFenceAsync(fence.Language, "", fence.Code, null, popup: true));
                last = fence.End;
            }

            builder.Append(html, last, html.Length - last);
            return builder.ToString();
        }

        private async ValueTask<string> RenderPopupAsync(PopupModel node)
        {
            var values = new Dictionary<string, FluidValue>(StringComparer.Ordinal)
            {
                ["kind"] = StringValue.Create(node.Kind),
                ["content"] = Raw(node.Content),
                ["type"] = node.Type == null ? NilValue.Instance : Raw(node.Type),
                ["docs"] = node.Docs == null ? NilValue.Instance : Raw(node.Docs),
                ["tags"] = ArrayValue.Create([.. node.Tags.Select(x => Object(new Dictionary<string, FluidValue>(StringComparer.Ordinal)
                {
                    ["name"] = StringValue.Create(x.Name),
                    ["html"] = x.Html == null ? NilValue.Instance : Raw(x.Html),
                    ["dash"] = BooleanValue.Create(x.Dash),
                }))]),
                ["text"] = Text(node.Text),
                ["code"] = Text(node.Code),
                ["level"] = Text(node.Level),
                ["name"] = Text(node.Name),
                ["character"] = NumberValue.Create(node.Character),
                ["completions"] = ArrayValue.Create([.. node.Completions.Select(x => Object(new Dictionary<string, FluidValue>(StringComparer.Ordinal)
                {
                    ["name"] = StringValue.Create(x.Name),
                    ["kind"] = StringValue.Create(x.Kind),
                    ["deprecated"] = BooleanValue.Create(x.Deprecated),
                }))]),
                ["completions_prefix"] = Text(node.CompletionsPrefix),
            };

            // A popup renders inside a line of a block: the block's value is restored after it.
            var code = templateContext.GetValue("code");
            templateContext.SetValue("node", Object(values));
            var html = (await popup.RenderAsync(templateContext, OutputEncoders.Html)).TrimEnd('\r', '\n');
            templateContext.SetValue("code", code);
            return html;
        }

        private ValueTask<List<Token>[]> TokenizeAsync(string code, string language)
        {
            var tokenizer = step._tokenizer!;
            var grammar = step._aliases.GetValueOrDefault(language) ?? language;

            if (!CodeTokenizer.IsPlain(grammar) && tokenizer.Find(grammar) == null)
            {
                step._unknownLanguages.AddOrUpdate(language, 1, (_, count) => count + 1);
                grammar = "";
            }

            var cache = context.Build.Cache;
            var key = Hash128.Combine(step._settingsKey, Hash128.OfStrings([grammar, code]));

            if (cache != null && cache.TryGetMemo(step.Name, key, out var memo) && TokenCodec.TryRead(memo, tokenizer.Themes.Count) is { } cached)
            {
                Memoized++;
                return ValueTask.FromResult(cached);
            }

            Tokenized++;
            var tokens = tokenizer.Tokenize(code, grammar);
            cache?.PutMemo(step.Name, key, TokenCodec.Write(tokens));
            return ValueTask.FromResult(tokens);
        }

        private static FluidValue Raw(string html) => StringValue.Create(html, false);

        private static FluidValue Text(string? text) => text == null ? NilValue.Instance : StringValue.Create(text);

        private static FluidValue Object(Dictionary<string, FluidValue> values) => DictionaryValue.Create(new FluidValueDictionaryFluidIndexable(values));

        private static FluidValue ToValue(CodeBlockModel model)
        {
            FluidValue Line(CodeLine line) => Object(new Dictionary<string, FluidValue>(StringComparer.Ordinal)
            {
                ["html"] = Raw(line.Html),
                ["text"] = StringValue.Create(line.Text),
                ["number"] = NumberValue.Create(line.Number),
                ["marker"] = Text(line.Marker),
                ["label"] = Text(line.Label),
                ["indent"] = NumberValue.Create(line.Indent),
            });

            var lines = model.Lines.Select(Line).ToArray();
            var rows = new List<FluidValue>();
            var index = 0;

            foreach (var section in model.Sections)
            {
                if (section.From - 1 > index)
                {
                    rows.Add(Object(new() { ["lines"] = ArrayValue.Create(lines[index..(section.From - 1)]), ["section"] = NilValue.Instance }));
                }

                rows.Add(Object(new()
                {
                    ["lines"] = ArrayValue.Create(lines[(section.From - 1)..section.To]),
                    ["section"] = Object(new()
                    {
                        ["style"] = StringValue.Create(section.Style),
                        ["indent"] = NumberValue.Create(section.Indent),
                        ["from"] = NumberValue.Create(section.From),
                        ["to"] = NumberValue.Create(section.To),
                    }),
                }));
                index = section.To;
            }

            if (index < lines.Length)
            {
                rows.Add(Object(new() { ["lines"] = ArrayValue.Create(lines[index..]), ["section"] = NilValue.Instance }));
            }

            var options = new Dictionary<string, FluidValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var option in model.Meta.Options)
            {
                if (option.Key.Length > 0)
                {
                    options[option.Key] = option.Kind == MetaKind.Boolean ? BooleanValue.Create(option.Boolean) : StringValue.Create(option.Value);
                }
            }

            var lastNumber = model.Lines.Count == 0 ? 1 : model.Lines[^1].Number;

            return Object(new Dictionary<string, FluidValue>(StringComparer.Ordinal)
            {
                ["language"] = StringValue.Create(model.Language),
                ["grammar"] = StringValue.Create(model.Grammar),
                ["meta"] = StringValue.Create(model.Meta.Raw),
                ["options"] = Object(options),
                ["title"] = Text(model.Title),
                ["frame"] = StringValue.Create(model.Frame),
                ["terminal"] = BooleanValue.Create(model.Frame == "terminal"),
                ["copy"] = StringValue.Create(model.Copy),
                ["wrap"] = BooleanValue.Create(model.Wrap),
                ["max_line"] = NumberValue.Create(model.MaxLineLength),
                ["line_numbers"] = BooleanValue.Create(model.LineNumbers),
                ["number_width"] = NumberValue.Create(Math.Max((model.Lines.Count == 0 ? 1 : model.Lines[0].Number).ToString(System.Globalization.CultureInfo.InvariantCulture).Length, lastNumber.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)),
                ["popup"] = BooleanValue.Create(model.Popup),
                ["lines"] = ArrayValue.Create(lines),
                ["rows"] = ArrayValue.Create(rows),
            });
        }
    }

    private sealed record Fence(int Start, int End, string Language, string Meta, string Code, string? Twoslash);

    [GeneratedRegex(@"\G<pre((?:\s+data-(?:lang|meta|twoslash)=""[^""]*"")*)\s*><code(?:\s[^>]*)?>")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"data-(lang|meta|twoslash)=""([^""]*)""")]
    private static partial Regex FenceAttribute();

    /// <summary>
    /// The fences of a page, as the <c>markdown</c> step writes them: a <c>&lt;pre&gt;</c> with only <c>data-lang</c>,
    /// <c>data-meta</c> and <c>data-twoslash</c> attributes (or none) whose only child is a <c>&lt;code&gt;</c> holding
    /// escaped text. Other <c>&lt;pre&gt;</c> elements (<c>class="mermaid"</c>, highlighted code) are left alone.
    /// </summary>
    private List<Fence> FindFences(string html)
    {
        var fences = new List<Fence>();
        var index = 0;

        while ((index = html.IndexOf("<pre", index, StringComparison.Ordinal)) >= 0)
        {
            var start = FenceStart().Match(html, index);

            if (!start.Success)
            {
                index += 4;
                continue;
            }

            var contentStart = start.Index + start.Length;
            var end = html.IndexOf("</code></pre>", contentStart, StringComparison.Ordinal);

            if (end < 0 || html.AsSpan(contentStart, end - contentStart).Contains('<'))
            {
                index = contentStart;
                continue;
            }

            string language = "", meta = "";
            string? twoslash = null;

            foreach (Match attribute in FenceAttribute().Matches(start.Groups[1].Value))
            {
                var value = WebUtility.HtmlDecode(attribute.Groups[2].Value);

                switch (attribute.Groups[1].Value)
                {
                    case "lang":
                        language = value;
                        break;
                    case "meta":
                        meta = value;
                        break;
                    default:
                        twoslash = value;
                        break;
                }
            }

            var code = WebUtility.HtmlDecode(html[contentStart..end]).ReplaceLineEndings("\n");

            if (code.EndsWith('\n'))
            {
                code = code[..^1];
            }

            fences.Add(new Fence(index, end + "</code></pre>".Length, language, meta, code, twoslash));
            index = end;
        }

        return fences;
    }

    /// <summary>
    /// The memo of a fence's tokens: per line, its tokens (start, end, then per theme the color and the font style).
    /// </summary>
    private static class TokenCodec
    {
        public static byte[] Write(List<Token>[] lines)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory, Encoding.UTF8);
            writer.Write(lines.Length);

            foreach (var line in lines)
            {
                writer.Write(line.Count);

                foreach (var token in line)
                {
                    writer.Write(token.Start);
                    writer.Write(token.End);

                    foreach (var style in token.Styles)
                    {
                        writer.Write(style.Color);
                        writer.Write((byte)style.FontStyle);
                    }
                }
            }

            writer.Flush();
            return memory.ToArray();
        }

        public static List<Token>[]? TryRead(byte[] bytes, int themes)
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
                var lines = new List<Token>[reader.ReadInt32()];
                var colors = new Dictionary<string, string>(StringComparer.Ordinal);

                for (var i = 0; i < lines.Length; i++)
                {
                    var count = reader.ReadInt32();
                    var line = new List<Token>(count);

                    for (var t = 0; t < count; t++)
                    {
                        var start = reader.ReadInt32();
                        var end = reader.ReadInt32();
                        var styles = new TokenStyle[themes];

                        for (var s = 0; s < themes; s++)
                        {
                            var color = reader.ReadString();
                            styles[s] = new TokenStyle(colors.TryGetValue(color, out var shared) ? shared : colors[color] = color, reader.ReadByte());
                        }

                        line.Add(new Token(start, end, styles));
                    }

                    lines[i] = line;
                }

                return lines;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }
    }
}
