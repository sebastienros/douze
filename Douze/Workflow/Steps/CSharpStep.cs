using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Douze.CodeHover;
using Douze.CodeHover.CSharp;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tools;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: csharp</c>. Checks the C# code fences of HTML pages with the Roslyn language server and adds to each the
/// hovers of its symbols (their signature and documentation) and its errors, in twoslash's shape
/// (<see cref="CodeFenceStep{TState}"/>), for the highlighter to render as the <c>twoslash</c> step's.
/// </summary>
/// <remarks>
/// <para>Snippets compile in generated projects (<see cref="CSharpProject"/>): <c>projects</c> maps names to their
/// <c>sdk</c> (<c>Microsoft.NET.Sdk</c> by default), <c>framework</c> (<c>net10.0</c>), <c>packages</c> (ids to
/// versions), <c>usings</c> (global usings, over the SDK's implicit ones), <c>properties</c> (MSBuild properties),
/// <c>files</c> (shared files of the source folder, such as stubs that snippets use), <c>context</c> (a class, of a
/// shared file, that statements run in: its fields are the variables that fragments use without declaring them),
/// <c>titles</c> (the fence titles, or their ends, that select the project: <c>Program.cs</c>) and <c>patterns</c>
/// (regular expressions, one of which the code of a fence without a matching title must match to select the project).
/// A fence uses the project of its meta's <c>project=name</c>, else the first whose titles match its <c>title</c>, else
/// the first whose patterns match its code, else <c>default</c> (an empty project when not declared). A snippet can be top-level statements, type declarations or class members:
/// <see cref="CSharpSnippet"/> wraps it.</para>
/// <para>Options: <c>version</c> (of the language server, the NuGet package <c>roslyn-language-server.&lt;rid&gt;</c>,
/// downloaded from <c>registry</c>, nuget.org by default, into <c>&lt;cache&gt;/tools/roslyn/&lt;version&gt;/</c> on
/// first use), or <c>path</c> (its <c>Microsoft.CodeAnalysis.LanguageServer.dll</c>); <c>dotnet</c> (the <c>dotnet</c>
/// of the .NET SDK that restores the projects and runs the server: <c>DOTNET_HOST_PATH</c>, or <c>dotnet</c> on the
/// <c>PATH</c>, by default); <c>trigger</c> (a word the meta of the fences to check must have; every fence of
/// <c>languages</c> by default); <c>ignore</c> (diagnostic codes neither reported nor rendered).</para>
/// <para>A snippet with errors fails the build with the page and the fence; with <c>errors: warn</c>, it is a warning
/// and the fence renders its errors.</para>
/// </remarks>
internal sealed partial class CSharpStep : CodeFenceStep<IReadOnlyDictionary<string, CSharpProject>>
{
    // Changes when what the step writes changes, so that memoized results are not reused.
    private const string FormatVersion = "1";

    private const string DefaultProject = "default";

    private readonly NuGetTool _tool = NuGetTool.RoslynLanguageServer;
    private readonly string _version;
    private readonly string? _path;
    private readonly string? _dotnet;
    private readonly Uri? _registry;
    private readonly string? _trigger;
    private readonly HashSet<string> _ignored;
    private readonly List<ProjectOptions> _projects = [];

    private sealed record ProjectOptions(CSharpProject Project, SortedDictionary<string, FPath> Files, IReadOnlyList<Regex> Patterns);

    public CSharpStep(StepDefinition definition) : base(definition, ["csharp", "cs", "c#"], "version", "path", "registry", "dotnet", "projects", "trigger", "ignore")
    {
        _version = Options.GetString("version") ?? _tool.DefaultVersion;
        _path = Options.GetString("path");
        _dotnet = Options.GetString("dotnet");
        _trigger = Options.GetString("trigger");
        _ignored = new HashSet<string>(Options.Has("ignore") ? Options.GetStrings("ignore") : [], StringComparer.Ordinal);

        if (Options.GetString("registry") is { } registry)
        {
            if (!Uri.TryCreate(registry.EndsWith('/') ? registry : registry + "/", UriKind.Absolute, out _registry) || _registry.Scheme is not ("http" or "https"))
            {
                throw Error($"option 'registry' of step '{Name}' must be an http or https url");
            }
        }

        if (definition.With["projects"] is { } projects)
        {
            if (projects is not NodeObject projectsObject)
            {
                throw Error($"option 'projects' of step '{Name}' must be a mapping of project names to projects");
            }

            foreach (var (name, value) in projectsObject)
            {
                _projects.Add(ReadProject(name, value));
            }
        }

        if (!_projects.Any(x => x.Project.Name == DefaultProject))
        {
            _projects.Add(ReadProject(DefaultProject, null));
        }
    }

    protected override string FenceKind => "csharp";

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
            throw new DouzeException($"step '{Name}': the language server '{path}' does not exist");
        }

        ToolIdentity = Hash128.Of(await File.ReadAllBytesAsync(path, ct));
    }

    /// <summary>
    /// The .NET SDK's <c>dotnet</c>, and the language server: the file of <c>path</c>, or the version Douze downloads.
    /// </summary>
    public override IEnumerable<Requirement> Requirements(BuildContext context)
    {
        var dotnetHint = $"install the .NET SDK (10 or later) from https://dot.net, or set the option 'dotnet' of step '{Name}'";
        yield return ExecutableRequirement(context, "dotnet", _dotnet ?? (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : null), dotnetHint);

        yield return _path == null
            ? new NuGetToolRequirement(_tool, _version, new ToolInstaller(context.ToolsRoot, _registry, (level, message) => context.View.Message(level, Name, message)), $"set the option 'path' of step '{Name}' to the language server's Microsoft.CodeAnalysis.LanguageServer.dll")
            : FileRequirement(_tool.Name, ResolvePath(context, _path), $"set the option 'path' of step '{Name}' to the language server, or remove it to download {_tool.Name} {_version}");
    }

    protected override bool Selects(string meta) => _trigger == null || Regex.IsMatch(meta, $@"(?<![\w-]){Regex.Escape(_trigger)}(?![\w-])");

    /// <summary>
    /// The language, the code and the project of a fence.
    /// </summary>
    protected override IEnumerable<string> FenceKeyParts(string language, string meta, string code) => [language, code, ProjectOf(meta, code)];

    [GeneratedRegex(@"(?<![\w-])project=(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex ProjectAttribute();

    [GeneratedRegex(@"(?<![\w-])title=(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex TitleAttribute();

    private static string? Attribute(Regex regex, string meta) =>
        regex.Match(meta) is { Success: true } match ? match.Groups.Values.Skip(1).First(x => x.Success).Value : null;

    /// <summary>
    /// The name of the project of a fence: its <c>project=</c>, else the first project whose titles match its title, else
    /// the first whose patterns match its code, else <c>default</c>.
    /// </summary>
    private string ProjectOf(string meta, string code)
    {
        if (Attribute(ProjectAttribute(), meta) is { } project)
        {
            return project;
        }

        if (Attribute(TitleAttribute(), meta) is { } title)
        {
            foreach (var options in _projects)
            {
                if (options.Project.Titles.Any(x => title.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    return options.Project.Name;
                }
            }
        }

        foreach (var options in _projects)
        {
            if (options.Patterns.Any(x => x.IsMatch(code)))
            {
                return options.Project.Name;
            }
        }

        return DefaultProject;
    }

    protected override async ValueTask<(IReadOnlyDictionary<string, CSharpProject> State, Hash128 Key)> PrepareAsync(StepContext context, CancellationToken ct)
    {
        var projects = new Dictionary<string, CSharpProject>(StringComparer.Ordinal);
        var parts = new List<string> { FormatVersion, ToolIdentity.ToString(), TabWidth.ToString(CultureInfo.InvariantCulture), Trim ? "trim" : "keep" };
        parts.AddRange(_ignored.Order(StringComparer.Ordinal).Select(x => "ignore:" + x));

        foreach (var options in _projects)
        {
            var project = options.Project with { Files = await ReadSharedFilesAsync(options.Files, context) };
            projects[project.Name] = project;
            parts.AddRange(project.KeyParts());
        }

        return (projects, Hash128.OfStrings(parts));
    }

    protected override async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<CodeFence> fences, IReadOnlyDictionary<string, CSharpProject> projects, StepContext context, CancellationToken ct)
    {
        var build = context.Build;
        var dotnet = await DotnetAsync(build, ct);
        var server = _path != null ? ResolvePath(build, _path) : await new ToolInstaller(build.ToolsRoot, _registry, context.Log).InstallAsync(_tool, _version, ct);
        context.Log(LogLevel.Info, $"checking {fences.Count} C# snippet(s) with the Roslyn language server {(_path != null ? server : _version)}");

        var outcomes = new CodeOutcome?[fences.Count];
        var requests = new List<CSharpSnippetRequest>();
        var indexes = new List<int>();

        for (var i = 0; i < fences.Count; i++)
        {
            var name = ProjectOf(fences[i].Meta, fences[i].Code);

            if (!projects.TryGetValue(name, out var project))
            {
                outcomes[i] = new CodeOutcome(null, $"unknown project '{name}' (the projects are {string.Join(", ", projects.Keys.Order(StringComparer.Ordinal))})");
                continue;
            }

            var title = Attribute(TitleAttribute(), fences[i].Meta);
            var fileName = title?.Split(' ', '/', '\\')[^1] is { } last && last.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? last : "snippet.cs";
            requests.Add(new CSharpSnippetRequest(fences[i].Code, project, fileName));
            indexes.Add(i);
        }

        if (requests.Count > 0)
        {
            var workRoot = Path.Combine(build.TempRoot, "csharp", Name);
            var checker = new CSharpChecker(dotnet, server, workRoot, Processes, Timeout, _ignored, context.Log, build.Trace, Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var checkedOutcomes = await checker.CheckAsync(requests, ct);

                for (var i = 0; i < indexes.Count; i++)
                {
                    outcomes[indexes[i]] = checkedOutcomes[i];
                }

                context.Log(LogLevel.Info, $"checked {requests.Count} C# snippet(s) in {Commands.ProcessRunner.FormatDuration(stopwatch.Elapsed)} with {checker.Processes} language server(s)");
            }
            finally
            {
                DeleteWorkFolder(build, workRoot);
            }
        }

        return [.. outcomes.Select(x => x!)];
    }

    /// <summary>
    /// The <c>dotnet</c> of a .NET SDK: <c>dotnet</c>, <c>DOTNET_HOST_PATH</c> or the <c>PATH</c>'s.
    /// </summary>
    private async Task<string> DotnetAsync(BuildContext build, CancellationToken ct)
    {
        var dotnet = _dotnet != null ? ResolvePath(build, _dotnet)
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host
            : "dotnet";

        string version;

        try
        {
            version = await VersionAsync(dotnet, ["--version"], "", ct);
        }
        catch (DouzeException)
        {
            version = "";
        }

        if (version.Length == 0 || !char.IsAsciiDigit(version[0]))
        {
            throw new DouzeException($"step '{Name}' requires the .NET SDK (10 or later): '{dotnet}' {(version.Length == 0 ? "cannot run" : $"has no SDK ({version})")}; install it from https://dot.net, or set the option 'dotnet'");
        }

        return dotnet;
    }

    private ProjectOptions ReadProject(string name, Node? node)
    {
        if (node != null && node is not NodeObject)
        {
            throw Error($"project '{name}' of step '{Name}' must be a mapping");
        }

        var project = node as NodeObject;
        string[] fields = ["sdk", "framework", "packages", "usings", "properties", "files", "context", "titles", "patterns"];

        foreach (var (field, _) in project ?? [])
        {
            if (!fields.Contains(field, StringComparer.Ordinal))
            {
                throw Error($"project '{name}' of step '{Name}' has an unknown field '{field}'{WorkflowValidationException.Suggest(field, fields)}");
            }
        }

        return new ProjectOptions(
            new CSharpProject(
                name,
                Text("sdk") ?? CSharpProject.DefaultSdk,
                Text("framework") ?? CSharpProject.DefaultFramework,
                Mapping("packages"),
                List("usings"),
                Mapping("properties"),
                [],
                Text("context"),
                List("titles"),
                List("patterns")),
            ReadFilesOption(project?["files"], $"projects.{name}.files"),
            [.. List("patterns").Select(Pattern)]);

        Regex Pattern(string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw Error($"pattern '{pattern}' of project '{name}' of step '{Name}' is not a regular expression: {e.Message}");
            }
        }

        // A string, a number or a boolean, as text.
        static string? Scalar(Node? node) => node is IFormattable value ? value.ToString(null, CultureInfo.InvariantCulture) : null;

        string? Text(string field) => project?[field] switch
        {
            null => null,
            NodeValue<string> s => s.Value,
            _ => throw Error($"field '{field}' of project '{name}' of step '{Name}' must be a string"),
        };

        IReadOnlyList<string> List(string field) => project?[field] switch
        {
            null => [],
            NodeArray array => [.. array.Select(x => Scalar(x) ?? throw Error($"field '{field}' of project '{name}' of step '{Name}' must be a list of strings"))],
            _ => throw Error($"field '{field}' of project '{name}' of step '{Name}' must be a list of strings"),
        };

        IReadOnlyDictionary<string, string> Mapping(string field) => project?[field] switch
        {
            null => new Dictionary<string, string>(),
            NodeObject mapping => mapping.ToDictionary(x => x.Key, x => Scalar(x.Value) ?? throw Error($"field '{field}' of project '{name}' of step '{Name}' must be a mapping of strings"), StringComparer.Ordinal),
            _ => throw Error($"field '{field}' of project '{name}' of step '{Name}' must be a mapping of strings"),
        };
    }
}
