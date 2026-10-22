using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Douze.CodeHover;
using Douze.CodeHover.Go;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tools;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: go</c>. Checks the Go code fences of HTML pages with gopls and adds to each the hovers of its symbols
/// (their signature and documentation) and its errors, in twoslash's shape (<see cref="CodeFenceStep{TState}"/>), for
/// the highlighter to render as the <c>twoslash</c> step's.
/// </summary>
/// <remarks>
/// <para>Snippets compile in generated modules (<see cref="GoModule"/>), each snippet a package of its own:
/// <c>modules</c> maps names to their <c>requires</c> (module paths to versions, for the snippets' third-party
/// imports), <c>files</c> (shared files of the source folder, such as a package of stubs that snippets import as
/// <c>snippets/&lt;folder&gt;</c>), <c>titles</c> (the fence titles, or their ends, that select the module:
/// <c>main.go</c>) and <c>patterns</c> (regular expressions, one of which the code of a fence without a matching title
/// must match to select the module). A fence uses the module of its meta's <c>module=name</c>, else the first whose
/// titles match its <c>title</c>, else the first whose patterns match its code, else <c>default</c> (a module without
/// requirements when not declared). A snippet can be a file, declarations without a <c>package</c> clause, or
/// statements: <see cref="GoSnippet"/> completes it.</para>
/// <para>Options: <c>version</c> (of gopls, built with <c>go install</c> into
/// <c>&lt;cache&gt;/tools/gopls/&lt;version&gt;/</c> on first use), or <c>path</c> (a gopls executable); <c>go</c> (the
/// <c>go</c> that builds gopls, adds the requirements and that gopls runs: the <c>PATH</c>'s by default);
/// <c>trigger</c> (a word the meta of the fences to check must have; every fence of <c>languages</c> by default);
/// <c>ignore</c> (diagnostic codes neither reported nor rendered: <c>UnusedVar</c>, <c>UnusedImport</c>).</para>
/// <para>A snippet with errors fails the build with the page and the fence; with <c>errors: warn</c>, it is a warning
/// and the fence renders its errors.</para>
/// </remarks>
internal sealed partial class GoStep : CodeFenceStep<GoStep.GoState>
{
    // Changes when what the step writes changes, so that memoized results are not reused.
    private const string FormatVersion = "1";

    private const string DefaultModule = "default";

    private readonly GoTool _tool = GoTool.Gopls;
    private readonly string _version;
    private readonly string? _path;
    private readonly string? _go;
    private readonly string? _trigger;
    private readonly HashSet<string> _ignored;
    private readonly List<ModuleOptions> _modules = [];

    private sealed record ModuleOptions(GoModule Module, SortedDictionary<string, FPath> Files, IReadOnlyList<Regex> Patterns);

    /// <summary>
    /// What a check needs: the <c>go</c> executable, the language version of the modules (empty when <c>go</c> cannot
    /// run: checking anything then fails) and the modules.
    /// </summary>
    internal sealed record GoState(string Go, string GoVersion, IReadOnlyDictionary<string, GoModule> Modules);

    public GoStep(StepDefinition definition) : base(definition, ["go", "golang"], "version", "path", "go", "modules", "trigger", "ignore")
    {
        _version = Options.GetString("version") is { } version ? version.StartsWith('v') ? version : "v" + version : _tool.DefaultVersion;
        _path = Options.GetString("path");
        _go = Options.GetString("go");
        _trigger = Options.GetString("trigger");
        _ignored = new HashSet<string>(Options.Has("ignore") ? Options.GetStrings("ignore") : [], StringComparer.Ordinal);

        if (definition.With["modules"] is { } modules)
        {
            if (modules is not NodeObject modulesObject)
            {
                throw Error($"option 'modules' of step '{Name}' must be a mapping of module names to modules");
            }

            foreach (var (name, value) in modulesObject)
            {
                _modules.Add(ReadModule(name, value));
            }
        }

        if (!_modules.Any(x => x.Module.Name == DefaultModule))
        {
            _modules.Add(ReadModule(DefaultModule, null));
        }
    }

    protected override string FenceKind => "go";

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
            throw new DouzeException($"step '{Name}': gopls '{path}' does not exist");
        }

        ToolIdentity = Hash128.Of(await File.ReadAllBytesAsync(path, ct));
    }

    /// <summary>
    /// The <c>go</c> executable, and gopls: the file of <c>path</c>, or the version Douze builds with <c>go install</c>.
    /// </summary>
    public override IEnumerable<Requirement> Requirements(BuildContext context)
    {
        yield return ExecutableRequirement(context, "go", _go, $"install Go from https://go.dev/dl, or set the option 'go' of step '{Name}'");

        if (_path != null)
        {
            yield return FileRequirement(_tool.Name, ResolvePath(context, _path), $"set the option 'path' of step '{Name}' to a gopls binary, or remove it to build gopls {_version}");
            yield break;
        }

        // The go that builds gopls, null when it is not found.
        var go = _go == null ? FindExecutable(context, "go") : ResolvePath(context, _go);
        go = go != null && File.Exists(go) ? go : null;

        yield return new GoToolRequirement(_tool, _version, new ToolInstaller(context.ToolsRoot, null, (level, message) => context.View.Message(level, Name, message)), go, $"install Go from https://go.dev/dl to build gopls, or set the option 'path' of step '{Name}' to a gopls binary");
    }

    protected override bool Selects(string meta) => _trigger == null || Regex.IsMatch(meta, $@"(?<![\w-]){Regex.Escape(_trigger)}(?![\w-])");

    /// <summary>
    /// The language, the code, the module and the file name of a fence.
    /// </summary>
    protected override IEnumerable<string> FenceKeyParts(string language, string meta, string code) => [language, code, ModuleOf(meta, code), FileNameOf(meta)];

    [GeneratedRegex(@"(?<![\w-])module=(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex ModuleAttribute();

    [GeneratedRegex(@"(?<![\w-])title=(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex TitleAttribute();

    private static string? Attribute(Regex regex, string meta) =>
        regex.Match(meta) is { Success: true } match ? match.Groups.Values.Skip(1).First(x => x.Success).Value : null;

    /// <summary>
    /// The file name of a fence in its package: the last part of its title when it is a <c>.go</c> file, else
    /// <c>main.go</c>.
    /// </summary>
    private static string FileNameOf(string meta) =>
        Attribute(TitleAttribute(), meta)?.Split(' ', '/', '\\')[^1] is { } last && last.EndsWith(".go", StringComparison.OrdinalIgnoreCase) && last.Length > 3 ? last : "main.go";

    /// <summary>
    /// The name of the module of a fence: its <c>module=</c>, else the first module whose titles match its title, else
    /// the first whose patterns match its code, else <c>default</c>.
    /// </summary>
    private string ModuleOf(string meta, string code)
    {
        if (Attribute(ModuleAttribute(), meta) is { } module)
        {
            return module;
        }

        if (Attribute(TitleAttribute(), meta) is { } title)
        {
            foreach (var options in _modules)
            {
                if (options.Module.Titles.Any(x => title.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    return options.Module.Name;
                }
            }
        }

        foreach (var options in _modules)
        {
            if (options.Patterns.Any(x => x.IsMatch(code)))
            {
                return options.Module.Name;
            }
        }

        return DefaultModule;
    }

    protected override async ValueTask<(GoState State, Hash128 Key)> PrepareAsync(StepContext context, CancellationToken ct)
    {
        // The Go version is part of the key: hovers and errors are those of its standard library and language.
        var go = _go != null ? ResolvePath(context.Build, _go) : "go";
        var goVersion = await GoVersionAsync(go, ct);
        var modules = new Dictionary<string, GoModule>(StringComparer.Ordinal);
        var parts = new List<string> { FormatVersion, ToolIdentity.ToString(), $"go:{goVersion}", TabWidth.ToString(CultureInfo.InvariantCulture), Trim ? "trim" : "keep" };
        parts.AddRange(_ignored.Order(StringComparer.Ordinal).Select(x => "ignore:" + x));

        foreach (var options in _modules)
        {
            var module = options.Module with { Files = await ReadSharedFilesAsync(options.Files, context) };
            modules[module.Name] = module;
            parts.AddRange(module.KeyParts());
        }

        return (new GoState(go, goVersion, modules), Hash128.OfStrings(parts));
    }

    protected override async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<CodeFence> fences, GoState state, StepContext context, CancellationToken ct)
    {
        var build = context.Build;

        if (state.GoVersion.Length == 0)
        {
            throw new DouzeException($"step '{Name}' requires Go: '{state.Go}' cannot run; install it from https://go.dev/dl, or set the option 'go'");
        }

        var gopls = _path != null ? ResolvePath(build, _path) : await new ToolInstaller(build.ToolsRoot, null, context.Log).InstallAsync(_tool, _version, state.Go, ct);
        context.Log(LogLevel.Info, $"checking {fences.Count} Go snippet(s) with gopls {(_path != null ? gopls : _version)}");

        var outcomes = new CodeOutcome?[fences.Count];
        var requests = new List<GoSnippetRequest>();
        var indexes = new List<int>();

        for (var i = 0; i < fences.Count; i++)
        {
            var name = ModuleOf(fences[i].Meta, fences[i].Code);

            if (!state.Modules.TryGetValue(name, out var module))
            {
                outcomes[i] = new CodeOutcome(null, $"unknown module '{name}' (the modules are {string.Join(", ", state.Modules.Keys.Order(StringComparer.Ordinal))})");
                continue;
            }

            requests.Add(new GoSnippetRequest(fences[i].Code, module, FileNameOf(fences[i].Meta)));
            indexes.Add(i);
        }

        if (requests.Count > 0)
        {
            var workRoot = Path.Combine(build.TempRoot, "go", Name);
            var checker = new GoChecker(state.Go, gopls, state.GoVersion, workRoot, Processes, Timeout, _ignored, context.Log, build.Trace, Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var checkedOutcomes = await checker.CheckAsync(requests, ct);

                for (var i = 0; i < indexes.Count; i++)
                {
                    outcomes[indexes[i]] = checkedOutcomes[i];
                }

                context.Log(LogLevel.Info, $"checked {requests.Count} Go snippet(s) in {Commands.ProcessRunner.FormatDuration(stopwatch.Elapsed)} with {checker.Processes} gopls");
            }
            finally
            {
                DeleteWorkFolder(build, workRoot);
            }
        }

        return [.. outcomes.Select(x => x!)];
    }

    /// <summary>
    /// The version of <paramref name="go"/> without its <c>go</c> prefix (<c>1.26.2</c>), or empty when it cannot run.
    /// </summary>
    private static async Task<string> GoVersionAsync(string go, CancellationToken ct)
    {
        var info = new ProcessStartInfo(go)
        {
            ArgumentList = { "env", "GOVERSION" },
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        GoChecker.SetEnvironment(info, go);

        try
        {
            using var process = Commands.ProcessRunner.Start(info, "");
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var version = output.Trim();
            return process.ExitCode == 0 && version.StartsWith("go", StringComparison.Ordinal) && version.Length > 2 && char.IsAsciiDigit(version[2]) ? version[2..] : "";
        }
        catch (DouzeException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^s\d+$")]
    private static partial Regex PackageFolder();

    private ModuleOptions ReadModule(string name, Node? node)
    {
        if (node != null && node is not NodeObject)
        {
            throw Error($"module '{name}' of step '{Name}' must be a mapping");
        }

        var module = node as NodeObject;
        string[] fields = ["requires", "files", "titles", "patterns"];

        foreach (var (field, _) in module ?? [])
        {
            if (!fields.Contains(field, StringComparer.Ordinal))
            {
                throw Error($"module '{name}' of step '{Name}' has an unknown field '{field}'{WorkflowValidationException.Suggest(field, fields)}");
            }
        }

        var files = ReadFilesOption(module?["files"], $"modules.{name}.files");

        foreach (var path in files.Keys)
        {
            if (path is "go.mod" or "go.sum" || PackageFolder().IsMatch(path.Split('/')[0]))
            {
                throw Error($"option 'modules.{name}.files' of step '{Name}' has a reserved path '{path}': go.mod, go.sum and the folders s<n> are the module's and its snippets'");
            }
        }

        return new ModuleOptions(
            new GoModule(name, Mapping("requires"), [], List("titles"), List("patterns")),
            files,
            [.. List("patterns").Select(Pattern)]);

        Regex Pattern(string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw Error($"pattern '{pattern}' of module '{name}' of step '{Name}' is not a regular expression: {e.Message}");
            }
        }

        // A string, a number or a boolean, as text.
        static string? Scalar(Node? node) => node is IFormattable value ? value.ToString(null, CultureInfo.InvariantCulture) : null;

        IReadOnlyList<string> List(string field) => module?[field] switch
        {
            null => [],
            NodeArray array => [.. array.Select(x => Scalar(x) ?? throw Error($"field '{field}' of module '{name}' of step '{Name}' must be a list of strings"))],
            _ => throw Error($"field '{field}' of module '{name}' of step '{Name}' must be a list of strings"),
        };

        IReadOnlyDictionary<string, string> Mapping(string field) => module?[field] switch
        {
            null => new Dictionary<string, string>(),
            NodeObject mapping => mapping.ToDictionary(x => x.Key, x => Scalar(x.Value) ?? throw Error($"field '{field}' of module '{name}' of step '{Name}' must be a mapping of strings"), StringComparer.Ordinal),
            _ => throw Error($"field '{field}' of module '{name}' of step '{Name}' must be a mapping of strings"),
        };
    }
}
