using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Douze.CodeHover;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tools;
using Douze.Twoslash;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: twoslash</c>. Computes the type information of the TypeScript code fences of HTML pages, as twoslash does,
/// with the language server of TypeScript 7 (a native binary) and without Node: every fence that the <c>markdown</c> step wrote as
/// <c>&lt;pre data-lang="ts" data-meta="... twoslash ..."&gt;</c> (a language of <c>with.languages</c>, the word
/// <c>twoslash</c> in its meta) gets a <c>data-twoslash</c> attribute holding twoslash's result for its code, in
/// twoslash's shape (<c>{ "code": ..., "nodes": [...] }</c>: the code without its notations, the hovers, queries,
/// errors, highlights, completions and tags with their positions), which the highlighter renders in place of running
/// twoslash. The fence itself is left as it is. What it shares with the other code steps is in
/// <see cref="CodeFenceStep{TState}"/>.
/// </summary>
/// <remarks>
/// <para>The step is a reduce: the fences of every page are checked at once, by one language server per set of
/// compiler options (<see cref="TwoslashChecker"/>). Each result is memoized in the disk cache by its code, language and
/// everything the step's results depend on (the engine and its version, the compiler options, the shared files, the
/// tags, the handbook options): a build checks only the fences whose result is not known, and starts no language server
/// when there is none (then it does not need the engine at all).</para>
/// <para>Options: <c>engine</c> (<c>typescript</c>, Microsoft's TypeScript 7, by default, or <c>tsrs</c>, its port to
/// Rust, which speaks the same protocol); <c>version</c> (of the engine, downloaded from the npm registry,
/// <c>registry</c>, into <c>&lt;cache&gt;/tools/&lt;engine&gt;/&lt;version&gt;/</c> on first use; Douze's pinned version
/// by default), or <c>path</c> (the engine's binary, relative to the source folder); <c>compiler</c> (tsconfig compiler options over twoslash's defaults);
/// <c>files</c> (shared files: a path next to each snippet to a file of the source folder, such as a declaration file
/// that snippets import); <c>tags</c> (the custom notations, <c>// @log: text</c>); <c>handbook</c> (twoslash's handbook
/// options for every snippet, such as <c>noErrorValidation</c>); <c>languages</c>; <c>tab-width</c> (tabs are replaced
/// by that many spaces first, as the highlighter does); <c>trim</c> (true by default: the spaces at the end of lines
/// and the blank lines around the code are removed first, as highlighters such as Expressive Code do before running
/// twoslash, so that the result is for the code they render); <c>processes</c> (language servers at once) and
/// <c>timeout</c> (seconds per language server).</para>
/// <para>A snippet that twoslash would reject (an error its <c>// @errors</c> does not list, a query that finds nothing)
/// fails the build with the page and the fence; with <c>errors: warn</c>, it is a warning, and the fence gets its result
/// when there is one (its errors are rendered).</para>
/// </remarks>
internal sealed partial class TwoslashStep : CodeFenceStep<List<SharedFile>>
{
    // Changes when what the step writes changes, so that memoized results are not reused.
    private const string FormatVersion = "1";

    private static readonly string[] DefaultTags = ["annotate", "log", "warn", "error"];

    private readonly NpmTool _tool;
    private readonly string _version;
    private readonly string? _path;
    private readonly Uri? _registry;
    private readonly SortedDictionary<string, string> _compilerOptions = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, FPath> _files;
    private readonly IReadOnlyList<string> _tags;
    private readonly HandbookOptions _handbook;

    public TwoslashStep(StepDefinition definition) : base(definition, ["ts", "tsx", "typescript"], "engine", "version", "path", "registry", "compiler", "files", "tags", "handbook")
    {
        _tool = Options.GetString("engine") switch
        {
            null or "typescript" => NpmTool.TypeScript,
            "tsrs" => NpmTool.Tsrs,
            var other => throw Error($"option 'engine' of step '{Name}' must be 'typescript' or 'tsrs', not '{other}'"),
        };
        _version = Options.GetString("version") ?? _tool.DefaultVersion;
        _path = Options.GetString("path");
        _tags = Options.Has("tags") ? Options.GetStrings("tags") : DefaultTags;

        if (Options.GetString("registry") is { } registry)
        {
            if (!Uri.TryCreate(registry, UriKind.Absolute, out _registry) || _registry.Scheme is not ("http" or "https"))
            {
                throw Error($"option 'registry' of step '{Name}' must be an http or https url");
            }
        }

        foreach (var (name, value) in TwoslashSettings.DefaultCompilerOptions)
        {
            _compilerOptions[name] = value;
        }

        if (definition.With["compiler"] is { } compiler)
        {
            if (compiler is not NodeObject compilerObject)
            {
                throw Error($"option 'compiler' of step '{Name}' must be a mapping of compiler options");
            }

            foreach (var (name, value) in compilerObject)
            {
                if (!CompilerOptionTable.TryGet(name, out var option))
                {
                    throw Error($"option 'compiler' of step '{Name}' has an unknown compiler option '{name}'{WorkflowValidationException.Suggest(name, [.. CompilerOptionTable.Names])}");
                }

                if (option.Values != null && (value is not NodeValue<string> text || !option.Values.Contains(text.Value.ToLowerInvariant())))
                {
                    throw Error($"option 'compiler' of step '{Name}' has an invalid '{name}': {string.Join(", ", option.Values)}");
                }

                _compilerOptions[option.Name] = ToJson(value);
            }
        }

        _files = ReadFilesOption(definition.With["files"], "files");

        var handbook = new HandbookOptions();

        if (definition.With["handbook"] is { } handbookNode)
        {
            if (handbookNode is not NodeObject handbookObject)
            {
                throw Error($"option 'handbook' of step '{Name}' must be a mapping of twoslash handbook options");
            }

            foreach (var (name, value) in handbookObject)
            {
                if (!TwoslashMarkup.HandbookOptionNames.Contains(name))
                {
                    throw Error($"option 'handbook' of step '{Name}' has an unknown option '{name}'{WorkflowValidationException.Suggest(name, [.. TwoslashMarkup.HandbookOptionNames])}");
                }

                var text = value switch
                {
                    NodeValue<bool> b => b.Value ? null : "false",
                    NodeArray array => string.Join(' ', array.Select(x => x?.ToString())),
                    NodeValue v => v.ToString(),
                    _ => null,
                };

                try
                {
                    handbook = handbook.With(name, text);
                }
                catch (TwoslashException e)
                {
                    throw Error($"option 'handbook' of step '{Name}': {e.Message}");
                }
            }
        }

        _handbook = handbook;
    }

    protected override string FenceKind => "twoslash";

    /// <summary>
    /// What identifies the engine that checks: its name and version, or the content of the binary given by <c>path</c>.
    /// </summary>
    public override async ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        if (_path == null)
        {
            ToolIdentity = Hash128.OfText($"{_tool.Name} {_version}");
            return;
        }

        var path = ResolvePath(context, _path);

        if (!File.Exists(path))
        {
            throw new DouzeException($"step '{Name}': the {_tool.Name} binary '{path}' does not exist");
        }

        ToolIdentity = Hash128.Of(await File.ReadAllBytesAsync(path, ct));
    }

    [GeneratedRegex(@"\btwoslash\b")]
    private static partial Regex Trigger();

    protected override bool Selects(string meta) => Trigger().IsMatch(meta);

    /// <summary>
    /// The shared files, and what a result depends on besides its code and language.
    /// </summary>
    protected override async ValueTask<(List<SharedFile> State, Hash128 Key)> PrepareAsync(StepContext context, CancellationToken ct)
    {
        var shared = await ReadSharedFilesAsync(_files, context);
        var parts = new List<string> { FormatVersion, ToolIdentity.ToString(), _handbook.Key, TabWidth.ToString(CultureInfo.InvariantCulture) };
        parts.Add(Trim ? "trim" : "keep");
        parts.AddRange(_compilerOptions.Select(x => $"{x.Key}={x.Value}"));
        parts.AddRange(_tags.Order(StringComparer.Ordinal).Select(x => "tag:" + x));
        parts.AddRange(shared.Select(x => $"file:{x.Path}={Hash128.Of(x.Content)}"));
        return (shared, Hash128.OfStrings(parts));
    }

    protected override async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<CodeFence> fences, List<SharedFile> shared, StepContext context, CancellationToken ct)
    {
        var build = context.Build;
        var executable = _path != null ? ResolvePath(build, _path) : await InstallAsync(context, ct);
        var version = await VersionAsync(executable, ["--version"], Path.GetFileNameWithoutExtension(executable), ct);
        context.Log(LogLevel.Info, $"checking {fences.Count} snippet(s) with {version} ({executable})");

        var settings = new TwoslashSettings(_compilerOptions, shared, _tags.ToHashSet(StringComparer.Ordinal), _handbook);
        var workRoot = Path.Combine(build.TempRoot, "twoslash", Name);
        var checker = new TwoslashChecker(executable, settings, workRoot, Processes, Timeout, context.Log);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outcomes = await checker.CheckAsync([.. fences.Select(x => new TwoslashSnippet(x.Code, x.Language))], ct);
            context.Log(LogLevel.Info, $"checked {fences.Count} snippet(s) in {Commands.ProcessRunner.FormatDuration(stopwatch.Elapsed)} with {checker.Processes} language server(s)");
            return outcomes;
        }
        finally
        {
            DeleteWorkFolder(build, workRoot);
        }
    }

    private async Task<string> InstallAsync(StepContext context, CancellationToken ct)
    {
        var installer = new ToolInstaller(ToolsRoot(context.Build), _registry, context.Log);
        return await installer.InstallAsync(_tool, _version, ct);
    }

    /// <summary>
    /// The JSON value of a compiler option of <c>with.compiler</c>.
    /// </summary>
    private static string ToJson(Node? value) => value switch
    {
        null => "null",
        NodeValue<bool> b => b.Value ? "true" : "false",
        NodeValue<string> s => TwoslashMarkup.JsonString(s.Value),
        NodeValue v and IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        NodeArray array => "[" + string.Join(",", array.Select(ToJson)) + "]",
        NodeObject obj => "{" + string.Join(",", obj.Select(x => TwoslashMarkup.JsonString(x.Key) + ":" + ToJson(x.Value))) + "}",
        _ => TwoslashMarkup.JsonString(value.ToString() ?? ""),
    };
}
