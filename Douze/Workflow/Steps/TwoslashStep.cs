using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
/// twoslash. The fence itself is left as it is.
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
internal sealed partial class TwoslashStep : Step, IReduceStep
{
    // Changes when what the step writes changes, so that memoized results are not reused.
    private const string FormatVersion = "1";

    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html"]);

    private static readonly string[] DefaultTags = ["annotate", "log", "warn", "error"];

    private readonly NpmTool _tool;
    private readonly string _version;
    private readonly bool _warn;
    private readonly string? _path;
    private readonly Uri? _registry;
    private readonly SortedDictionary<string, string> _compilerOptions = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, FPath> _files = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> _tags;
    private readonly HandbookOptions _handbook;
    private readonly HashSet<string> _languages;
    private readonly int _tabWidth;
    private readonly bool _trim;
    private readonly int _processes;
    private readonly TimeSpan _timeout;

    private Hash128 _toolIdentity;

    public TwoslashStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "engine", "version", "path", "errors", "registry", "compiler", "files", "tags", "handbook", "languages", "tab-width", "trim", "processes", "timeout");

        _tool = options.GetString("engine") switch
        {
            null or "typescript" => NpmTool.TypeScript,
            "tsrs" => NpmTool.Tsrs,
            var other => throw Error($"option 'engine' of step '{Name}' must be 'typescript' or 'tsrs', not '{other}'"),
        };
        _version = options.GetString("version") ?? _tool.DefaultVersion;
        _warn = options.GetString("errors") switch
        {
            null or "fail" => false,
            "warn" => true,
            var other => throw Error($"option 'errors' of step '{Name}' must be 'fail' or 'warn', not '{other}'"),
        };
        _path = options.GetString("path");
        _tags = options.Has("tags") ? options.GetStrings("tags") : DefaultTags;
        _languages = new HashSet<string>(options.Has("languages") ? options.GetStrings("languages") : ["ts", "tsx", "typescript"], StringComparer.Ordinal);
        _tabWidth = options.GetInt("tab-width", 0);
        _trim = options.GetBool("trim", true);
        _processes = options.GetInt("processes", Math.Min(4, Environment.ProcessorCount));
        _timeout = TimeSpan.FromSeconds(options.GetInt("timeout", 300));

        if (options.GetString("registry") is { } registry)
        {
            if (!Uri.TryCreate(registry, UriKind.Absolute, out _registry) || _registry.Scheme is not ("http" or "https"))
            {
                throw Error($"option 'registry' of step '{Name}' must be an http or https url");
            }
        }

        if (_tabWidth is < 0 or > 16 || _processes < 1 || _timeout <= TimeSpan.Zero)
        {
            throw Error($"options 'tab-width' (0 to 16), 'processes' and 'timeout' (at least 1) of step '{Name}' are out of range");
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

        if (definition.With["files"] is { } files)
        {
            if (files is not NodeObject filesObject || filesObject.Any(x => x.Value is not NodeValue<string>))
            {
                throw Error($"option 'files' of step '{Name}' must be a mapping of paths next to the snippets to files of the source folder");
            }

            foreach (var (path, source) in filesObject)
            {
                var relative = path.Replace('\\', '/');

                if (relative.Length == 0 || relative.StartsWith('/') || relative.Split('/').Any(x => x is ".." or "."))
                {
                    throw Error($"option 'files' of step '{Name}' has an invalid path '{path}': it must be relative, without '.' or '..'");
                }

                _files[relative] = new FPath(((NodeValue<string>)source!).Value).ToRelative();
            }
        }

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

        WorkflowValidationException Error(string message) => new(message, definition.Location);
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => Pages;

    public override Hash128 CacheVersion => _toolIdentity;

    /// <summary>
    /// What identifies the engine that checks: its name and version, or the content of the binary given by <c>path</c>.
    /// </summary>
    public override async ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        if (_path == null)
        {
            _toolIdentity = Hash128.OfText($"{_tool.Name} {_version}");
            return;
        }

        var path = ResolvePath(context);

        if (!File.Exists(path))
        {
            throw new DouzeException($"step '{Name}': the {_tool.Name} binary '{path}' does not exist");
        }

        _toolIdentity = Hash128.Of(await File.ReadAllBytesAsync(path, ct));
    }

    private string ResolvePath(BuildContext context)
    {
        return Path.IsPathRooted(_path!) || context.Site.SourceDirectory is not { } source ? Path.GetFullPath(_path!) : Path.GetFullPath(Path.Combine(source, _path!));
    }

    private sealed record Fence(int File, int Index, int TagEnd, string Language, string Code, Hash128 Key);

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var shared = await ReadSharedFilesAsync(context);
        var settingsKey = SettingsKey(shared);
        var texts = new string?[files.Count];
        var fences = new List<Fence>();

        for (var i = 0; i < files.Count; i++)
        {
            if (!files[i].Content.IsText)
            {
                continue;
            }

            var text = await files[i].Content.ReadTextAsync(ct);

            if (!text.Contains("<pre", StringComparison.Ordinal))
            {
                continue;
            }

            texts[i] = text;

            var index = 0;

            foreach (var (tagEnd, language, code) in FindFences(text))
            {
                fences.Add(new Fence(i, ++index, tagEnd, language, code, Hash128.Combine(settingsKey, Hash128.OfStrings([language, code]))));
            }
        }

        if (fences.Count == 0)
        {
            return new StepOutput(FileList.Of(files));
        }

        // The results: memoized ones, then the others, checked at once.
        var results = new Dictionary<Hash128, string>();
        var cache = context.Build.Cache;
        var missing = new List<Fence>();
        var missingKeys = new HashSet<Hash128>();

        foreach (var fence in fences)
        {
            if (results.ContainsKey(fence.Key) || missingKeys.Contains(fence.Key))
            {
                continue;
            }

            if (cache != null && cache.TryGetMemo(Name, fence.Key, out var memo))
            {
                results[fence.Key] = Encoding.UTF8.GetString(memo);
            }
            else
            {
                missing.Add(fence);
                missingKeys.Add(fence.Key);
            }
        }

        if (missing.Count > 0)
        {
            var errors = new List<string>();
            var outcomes = await CheckAsync(missing, shared, context, ct);
            var byKey = fences.ToLookup(x => x.Key);

            for (var i = 0; i < missing.Count; i++)
            {
                var (result, error) = outcomes[i];

                if (error != null)
                {
                    // Every fence with that code is reported, by its page's source and its number on the page. Its
                    // result is not memoized, so that the next build that checks it reports it again.
                    errors.AddRange(byKey[missing[i].Key].Select(x => $"{files[x.File].SourcePath ?? files[x.File].Path}: twoslash fence {x.Index} ('{FirstLine(x.Code)}'): {error}"));

                    if (_warn && result != null)
                    {
                        results[missing[i].Key] = result.ToJson();
                    }
                }
                else if (result != null)
                {
                    var json = result.ToJson();
                    results[missing[i].Key] = json;
                    cache?.PutMemo(Name, missing[i].Key, Encoding.UTF8.GetBytes(json));
                }
            }

            errors.Sort(StringComparer.Ordinal);

            if (errors.Count > 0 && !_warn)
            {
                throw new DouzeException(string.Join(Environment.NewLine, errors));
            }

            foreach (var error in errors)
            {
                context.Log(LogLevel.Warning, error);
            }
        }

        var outputs = new VirtualFile[files.Count];
        var byFile = fences.ToLookup(x => x.File);

        for (var i = 0; i < files.Count; i++)
        {
            var fileFences = byFile[i].Where(x => results.ContainsKey(x.Key)).ToList();

            if (fileFences.Count == 0)
            {
                outputs[i] = files[i];
                continue;
            }

            var text = texts[i]!;
            var builder = new StringBuilder(text.Length + fileFences.Sum(x => results[x.Key].Length + 32));
            var last = 0;

            foreach (var fence in fileFences)
            {
                // Before the '>' of the <pre> tag.
                builder.Append(text, last, fence.TagEnd - last);
                builder.Append(" data-twoslash=\"").Append(WebUtility.HtmlEncode(results[fence.Key])).Append('"');
                last = fence.TagEnd;
            }

            builder.Append(text, last, text.Length - last);
            outputs[i] = files[i].With(Name, content: FileContent.FromText(builder.ToString()));
        }

        return new StepOutput(FileList.Of(outputs));
    }

    private async Task<IReadOnlyList<TwoslashOutcome>> CheckAsync(List<Fence> fences, List<SharedFile> shared, StepContext context, CancellationToken ct)
    {
        var build = context.Build;
        var executable = _path != null ? ResolvePath(build) : await InstallAsync(context, ct);
        var version = await VersionAsync(executable, ct);
        context.Log(LogLevel.Info, $"checking {fences.Count} snippet(s) with {version} ({executable})");

        var settings = new TwoslashSettings(_compilerOptions, shared, _tags.ToHashSet(StringComparer.Ordinal), _handbook);
        var workRoot = Path.Combine(build.TempRoot, "twoslash", Name);
        var checker = new TwoslashChecker(executable, settings, workRoot, _processes, _timeout, context.Log);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outcomes = await checker.CheckAsync([.. fences.Select(x => new TwoslashSnippet(x.Code, x.Language))], ct);
            context.Log(LogLevel.Info, $"checked {fences.Count} snippet(s) in {Commands.ProcessRunner.FormatDuration(stopwatch.Elapsed)} with {checker.Processes} language server(s)");
            return outcomes;
        }
        finally
        {
            if (!build.Options.Verbose)
            {
                try
                {
                    Directory.Delete(workRoot, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task<string> InstallAsync(StepContext context, CancellationToken ct)
    {
        var build = context.Build;
        var root = Path.Combine(build.CacheRoot ?? Path.Combine(Path.GetTempPath(), BuildContext.DefaultCacheFolder), "tools");
        var installer = new ToolInstaller(root, _registry, context.Log);
        return await installer.InstallAsync(_tool, _version, ct);
    }

    private static string FirstLine(string code)
    {
        var line = code.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }

    /// <summary>
    /// The first line of <c>--version</c> of the engine.
    /// </summary>
    private static async Task<string> VersionAsync(string executable, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { ArgumentList = { "--version" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Commands.ProcessRunner.Start(info, "");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output.Split('\n')[0].Trim() is { Length: > 0 } line ? line : Path.GetFileNameWithoutExtension(executable);
    }

    private async Task<List<SharedFile>> ReadSharedFilesAsync(StepContext context)
    {
        var shared = new List<SharedFile>(_files.Count);

        foreach (var (path, source) in _files)
        {
            var fullPath = FPath.Combine(FPath.Root, source);
            byte[] bytes;

            try
            {
                await using var stream = await context.Build.Source.OpenFileAsync(fullPath);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                context.Dependencies.RecordTemplate(fullPath, DependencySet.Absent);
                throw new DouzeException($"step '{Name}': the file '{source}' of option 'files' does not exist");
            }

            context.Dependencies.RecordTemplate(fullPath, Hash128.Of(bytes));
            shared.Add(new SharedFile(path, bytes));
        }

        return shared;
    }

    /// <summary>
    /// What a result depends on besides its code and language.
    /// </summary>
    private Hash128 SettingsKey(List<SharedFile> shared)
    {
        var parts = new List<string> { FormatVersion, _toolIdentity.ToString(), _handbook.Key, _tabWidth.ToString(CultureInfo.InvariantCulture) };
        parts.Add(_trim ? "trim" : "keep");
        parts.AddRange(_compilerOptions.Select(x => $"{x.Key}={x.Value}"));
        parts.AddRange(_tags.Order(StringComparer.Ordinal).Select(x => "tag:" + x));
        parts.AddRange(shared.Select(x => $"file:{x.Path}={Hash128.Of(x.Content)}"));
        return Hash128.OfStrings(parts);
    }

    [GeneratedRegex(@"\Gpre((?:\s+data-(?:lang|meta)=""[^""]*"")*)\s*>")]
    private static partial Regex PreTag();

    [GeneratedRegex(@"\G<code(?:\s[^>]*)?>")]
    private static partial Regex CodeTag();

    [GeneratedRegex(@"data-(lang|meta)=""([^""]*)""")]
    private static partial Regex PreAttribute();

    [GeneratedRegex(@"\btwoslash\b")]
    private static partial Regex Trigger();

    /// <summary>
    /// The twoslash fences of a page, as the <c>markdown</c> step writes fences: a <c>&lt;pre&gt;</c> with only
    /// <c>data-lang</c> and <c>data-meta</c> attributes whose only child is a <c>&lt;code&gt;</c> holding escaped text.
    /// Returns where the <c>&lt;pre</c> tag's attributes end, the language and the decoded code (without the last line
    /// end, tabs replaced as <c>tab-width</c> says, trimmed as <c>trim</c> says).
    /// </summary>
    private IEnumerable<(int TagEnd, string Language, string Code)> FindFences(string html)
    {
        var index = 0;

        while ((index = html.IndexOf("<pre", index, StringComparison.Ordinal)) >= 0)
        {
            var pre = PreTag().Match(html, index + 1);

            if (!pre.Success)
            {
                index += 4;
                continue;
            }

            var code = CodeTag().Match(html, pre.Index + pre.Length);
            var end = code.Success ? html.IndexOf("</code></pre>", code.Index + code.Length, StringComparison.Ordinal) : -1;

            if (end < 0)
            {
                index = pre.Index + pre.Length;
                continue;
            }

            var language = "";
            var meta = "";

            foreach (Match attribute in PreAttribute().Matches(pre.Groups[1].Value))
            {
                var value = WebUtility.HtmlDecode(attribute.Groups[2].Value);

                if (attribute.Groups[1].Value == "lang")
                {
                    language = value;
                }
                else
                {
                    meta = value;
                }
            }

            index = end;
            var text = html.AsSpan(code.Index + code.Length, end - code.Index - code.Length);

            if (!_languages.Contains(language) || !Trigger().IsMatch(meta) || text.Contains('<'))
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(text.ToString()).ReplaceLineEndings("\n");

            if (decoded.EndsWith('\n'))
            {
                decoded = decoded[..^1];
            }

            if (_tabWidth > 0)
            {
                decoded = decoded.Replace("\t", new string(' ', _tabWidth), StringComparison.Ordinal);
            }

            if (_trim)
            {
                decoded = Trim(decoded);
            }

            // The end of the attributes: before the '>' of the tag, and its spaces.
            var tagEnd = pre.Index + pre.Length - 1;

            while (char.IsWhiteSpace(html[tagEnd - 1]))
            {
                tagEnd--;
            }

            yield return (tagEnd, language, decoded);
        }
    }

    /// <summary>
    /// The code with the white space at the end of its lines and its leading and trailing blank lines removed.
    /// </summary>
    private static string Trim(string code)
    {
        var lines = code.Split('\n').Select(x => x.TrimEnd()).ToList();
        var start = lines.FindIndex(x => x.Length > 0);

        if (start < 0)
        {
            return "";
        }

        var end = lines.FindLastIndex(x => x.Length > 0);
        return string.Join('\n', lines.Skip(start).Take(end - start + 1));
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
