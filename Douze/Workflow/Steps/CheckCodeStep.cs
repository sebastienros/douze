using System.Text;
using Douze.Highlighting;
using Douze.Nodes;
using Douze.Progress;
using Douze.Validation;
using Fluid;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: check-code</c>. Checks that code is well formed, with in-process parsers by language
/// (<see cref="CodeValidators"/>): in an HTML page, the fences that the <c>markdown</c> step wrote (so it runs before
/// <c>highlight</c>); in any other file, its whole content, by the language of its extension (a feed after
/// <c>layout</c>). A YAML fence titled, or a YAML file named, as <c>workflow</c> says (<c>douze.yml</c>) is also checked
/// against the workflow schema (<see cref="WorkflowSnippet"/>). Files are returned as they are.
/// </summary>
/// <remarks>
/// Options: <c>languages</c> (the validators that run, all by default), <c>aliases</c> (more fence languages and
/// extensions for a validator), <c>workflow</c> (the fence titles and file names checked as a workflow), <c>skip</c>
/// (the meta word that leaves a fence out, <c>nocheck</c>), <c>ignore</c> (globs of the paths left out) and
/// <c>errors</c> (<c>fail</c>, or <c>warn</c>). A page is checked again only when its content changed: the unit is
/// cached by the scheduler. Its warnings are kept in a memo and logged again when the unit is replayed.
/// </remarks>
internal sealed class CheckCodeStep : Step, IMapStep, IReplayObserver
{
    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html"]);

    private readonly Dictionary<string, string> _validators = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workflowNames;
    private readonly string _skip;
    private readonly GlobSet? _ignore;
    private readonly bool _warn;
    private BuildContext? _build;
    private FluidParser? _parser;
    private Hash128 _cacheVersion;

    public CheckCodeStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "languages", "aliases", "workflow", "skip", "ignore", "errors");

        _warn = options.GetString("errors") switch
        {
            null or "fail" => false,
            "warn" => true,
            var other => throw Error($"option 'errors' of step '{Name}' must be 'fail' or 'warn', not '{other}'"),
        };

        var languages = options.Has("languages") ? options.GetStrings("languages") : CodeValidators.Names;

        foreach (var language in languages)
        {
            if (!CodeValidators.Names.Contains(language))
            {
                throw Error($"unknown language '{language}' in option 'languages' of step '{Name}'{WorkflowValidationException.Suggest(language, CodeValidators.Names)}; expected {Expected()}");
            }
        }

        foreach (var (alias, validator) in CodeValidators.BuiltInAliases)
        {
            if (languages.Contains(validator))
            {
                _validators[alias] = validator;
            }
        }

        if (definition.With["aliases"] is { } aliases)
        {
            if (aliases is not NodeObject map || map.Any(x => x.Value is not NodeValue<string>))
            {
                throw Error($"option 'aliases' of step '{Name}' must be a mapping of fence languages to validators");
            }

            foreach (var (alias, value) in map)
            {
                var validator = ((NodeValue<string>)value!).Value;

                if (!CodeValidators.Names.Contains(validator))
                {
                    throw Error($"unknown validator '{validator}' for alias '{alias}' of step '{Name}'{WorkflowValidationException.Suggest(validator, CodeValidators.Names)}; expected {Expected()}");
                }

                if (CodeValidators.BuiltInAliases.TryGetValue(alias, out var builtIn) && builtIn != validator)
                {
                    throw Error($"alias '{alias}' of step '{Name}' is checked by '{builtIn}' already");
                }

                if (languages.Contains(validator))
                {
                    _validators[alias] = validator;
                }
            }
        }

        _workflowNames = new HashSet<string>(options.Has("workflow") ? options.GetStrings("workflow") : ["douze.yml"], StringComparer.Ordinal);
        _skip = options.GetString("skip", "nocheck")!;
        _ignore = options.GetGlobs("ignore");
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => Pages;

    public override bool ReadsScopes => false;

    // A new shortcode makes a Liquid sample valid.
    public override Hash128 CacheVersion => _cacheVersion;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;
        _parser = context.Templates.Parser;
        _cacheVersion = _validators.ContainsValue(CodeValidators.Liquid) ? context.Shortcodes.Hash : Hash128.Empty;
        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!file.Content.IsText || _ignore?.IsMatch(file.Path) == true)
        {
            return FileList.Of(file);
        }

        var errors = new List<string>();
        var extension = Path.GetExtension(file.Path.FullName).TrimStart('.').ToLowerInvariant();

        if (extension == "html")
        {
            var text = await file.Content.ReadTextAsync(ct);

            if (text.Contains("<pre", StringComparison.Ordinal))
            {
                var page = file.SourcePath?.FullName ?? file.Path.FullName;
                var index = 0;

                foreach (var (_, language, meta, code) in CodeFenceScanner.FindFences(text, (language, meta) => _validators.ContainsKey(language) && !Skips(meta), 0, false))
                {
                    index++;
                    var title = new CodeMeta(meta).GetString("title");
                    var workflow = title != null && (_workflowNames.Contains(title) || _workflowNames.Contains(Path.GetFileName(title)));

                    foreach (var error in await CheckAsync(_validators[language], code, workflow, fragment: true))
                    {
                        errors.Add($"{page}: {language} fence {index} ('{CodeFenceScanner.FirstLine(code)}'): {error}");
                    }
                }
            }
        }
        else if (_validators.TryGetValue(extension, out var validator))
        {
            var text = await file.Content.ReadTextAsync(ct);
            var name = file.SourcePath is { } source && source != file.Path ? $"{file.Path.FullName} ({source.FullName})" : file.Path.FullName;
            var workflow = _workflowNames.Contains(Path.GetFileName(file.Path.FullName));

            foreach (var error in await CheckAsync(validator, text, workflow, fragment: false))
            {
                errors.Add($"{name}: {validator}: {error}");
            }
        }

        if (errors.Count == 0)
        {
            return FileList.Of(file);
        }

        if (!_warn)
        {
            throw new DouzeException(string.Join(Environment.NewLine, errors));
        }

        foreach (var error in errors)
        {
            context.Log(LogLevel.Warning, error);
        }

        context.Build.Cache?.PutMemo(Name, WarningsKey(file), Encoding.UTF8.GetBytes(string.Join('\n', errors)));
        return FileList.Of(file);
    }

    /// <summary>
    /// Logs the warnings of a replayed page again.
    /// </summary>
    public void Replayed(IReadOnlyList<VirtualFile> inputs, IReadOnlyList<VirtualFile> outputs)
    {
        if (!_warn || _build?.Cache is not { } cache)
        {
            return;
        }

        foreach (var input in inputs)
        {
            if (cache.TryGetMemo(Name, WarningsKey(input), out var memo))
            {
                foreach (var warning in Encoding.UTF8.GetString(memo).Split('\n'))
                {
                    _build.View.Message(LogLevel.Warning, Name, warning);
                }
            }
        }
    }

    private Hash128 WarningsKey(VirtualFile file) =>
        Hash128.Combine(Definition.ConfigHash, Hash128.OfText(file.Path.FullName), file.ContentHash);

    private bool Skips(string meta) => meta.Length > 0 && new CodeMeta(meta).GetBoolean(_skip) == true;

    private async ValueTask<IReadOnlyList<CodeError>> CheckAsync(string validator, string code, bool workflow, bool fragment)
    {
        switch (validator)
        {
            case CodeValidators.Yaml:
                var errors = CodeValidators.CheckYaml(code, out var stream);
                return errors.Count == 0 && workflow ? await WorkflowSnippet.CheckAsync(code, stream!) : errors;

            case CodeValidators.Json:
            case CodeValidators.Jsonc:
                return CodeValidators.CheckJson(code, comments: validator == CodeValidators.Jsonc);

            case CodeValidators.Jsonl:
                return CodeValidators.CheckJsonLines(code);

            case CodeValidators.Xml:
                return CodeValidators.CheckXml(code, fragment);

            default:
                return CodeValidators.CheckLiquid(code, _parser!);
        }
    }

    private static string Expected() => string.Join(", ", CodeValidators.Names.Select(x => $"'{x}'"));

    private WorkflowValidationException Error(string message) => new(message, Definition.Location);
}
