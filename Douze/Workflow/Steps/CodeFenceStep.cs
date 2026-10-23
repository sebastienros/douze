using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Douze.CodeHover;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Steps;

/// <summary>
/// A code fence of a page that a <see cref="CodeFenceStep{TState}"/> checks: the page (its index in the step's input),
/// its number on the page, where the attributes of its <c>&lt;pre&gt;</c> tag end, its language and meta, its code and
/// the key of its memoized result.
/// </summary>
internal sealed record CodeFence(int File, int Index, int TagEnd, string Language, string Meta, string Code, Hash128 Key);

/// <summary>
/// What the steps that check code fences with a language server share (<c>twoslash</c>, <c>csharp</c>, <c>go</c>):
/// every fence that the <c>markdown</c> step wrote as <c>&lt;pre data-lang="..." data-meta="..."&gt;</c> and that the
/// step selects (by its language and meta) gets a <c>data-twoslash</c> attribute holding the result for its code in
/// twoslash's shape (<see cref="TwoslashResult"/>), which the highlighter renders: the hovers and the errors, whatever
/// the language. The fence itself is left as it is.
/// </summary>
/// <remarks>
/// <para>The step is a reduce: the fences of every page are checked at once, by the step's engine. Each outcome (the
/// result, the errors) is memoized in the disk cache by the fence's code, language (and whatever else of the fence it
/// depends on: <see cref="FenceKeyParts"/>) and the key of the step's settings (<see cref="PrepareAsync"/>): a build
/// checks only the fences whose outcome is not known, and starts no engine when there is none. A memoized error is
/// reported again by every build that uses it.</para>
/// <para>Shared options: <c>errors</c> (<c>fail</c>, the default, or <c>warn</c>: a rejected snippet is a warning, and
/// its fence gets its result when there is one, its errors rendered); <c>languages</c>; <c>tab-width</c> (tabs are
/// replaced by that many spaces first, as the highlighter does); <c>trim</c> (true by default: the spaces at the end of
/// lines and the blank lines around the code are removed first, as highlighters such as Expressive Code do, so that the
/// result is for the code they render); <c>processes</c> (language servers at once) and <c>timeout</c> (seconds per
/// language server).</para>
/// </remarks>
/// <typeparam name="TState">What a build's check needs besides the fences, from <see cref="PrepareAsync"/> (such as the
/// shared files it read).</typeparam>
internal abstract partial class CodeFenceStep<TState> : Step, IReduceStep
{
    private static readonly GlobSet Pages = GlobSet.Parse(["**/*.html"]);

    private static readonly string[] SharedOptions = ["errors", "languages", "tab-width", "trim", "processes", "timeout"];

    private readonly HashSet<string> _languages;

    /// <param name="definition">The step.</param>
    /// <param name="defaultLanguages">The fence languages checked when <c>languages</c> is not set.</param>
    /// <param name="options">The step's own options, besides the shared ones.</param>
    protected CodeFenceStep(StepDefinition definition, string[] defaultLanguages, params string[] options) : base(definition)
    {
        Options = new WithOptions(definition, [.. SharedOptions, .. options]);

        Warn = Options.GetString("errors") switch
        {
            null or "fail" => false,
            "warn" => true,
            var other => throw Error($"option 'errors' of step '{Name}' must be 'fail' or 'warn', not '{other}'"),
        };
        _languages = new HashSet<string>(Options.Has("languages") ? Options.GetStrings("languages") : defaultLanguages, StringComparer.Ordinal);
        TabWidth = Options.GetInt("tab-width", 0);
        Trim = Options.GetBool("trim", true);
        Processes = Options.GetInt("processes", Math.Min(4, Environment.ProcessorCount));
        Timeout = TimeSpan.FromSeconds(Options.GetInt("timeout", 300));

        if (TabWidth is < 0 or > 16 || Processes < 1 || Timeout <= TimeSpan.Zero)
        {
            throw Error($"options 'tab-width' (0 to 16), 'processes' and 'timeout' (at least 1) of step '{Name}' are out of range");
        }
    }

    protected WithOptions Options { get; }

    protected bool Warn { get; }

    protected int TabWidth { get; }

    protected bool Trim { get; }

    protected int Processes { get; }

    protected TimeSpan Timeout { get; }

    /// <summary>
    /// What identifies the engine, set by <see cref="Step.InitializeAsync"/>: its name and version, or the content of its
    /// binary.
    /// </summary>
    protected Hash128 ToolIdentity { get; set; }

    /// <summary>
    /// How the step's fences are named in its errors: <c>"twoslash"</c> gives <c>twoslash fence 2</c>.
    /// </summary>
    protected abstract string FenceKind { get; }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => Pages;

    public override Hash128 CacheVersion => ToolIdentity;

    /// <summary>
    /// Whether the step checks a fence of a language of <c>languages</c> with this meta.
    /// </summary>
    protected abstract bool Selects(string meta);

    /// <summary>
    /// What a fence's result depends on besides the settings: its language and code by default.
    /// </summary>
    protected virtual IEnumerable<string> FenceKeyParts(string language, string meta, string code) => [language, code];

    /// <summary>
    /// Reads what a build's check needs (recording its dependencies) and returns it with the key of everything the
    /// results depend on besides the fences (the engine, the options, the shared files).
    /// </summary>
    protected abstract ValueTask<(TState State, Hash128 Key)> PrepareAsync(StepContext context, CancellationToken ct);

    /// <summary>
    /// Checks fences whose results are not memoized and returns their outcomes in the same order.
    /// </summary>
    protected abstract Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<CodeFence> fences, TState state, StepContext context, CancellationToken ct);

    protected WorkflowValidationException Error(string message) => new(message, Definition.Location);

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var (state, settingsKey) = await PrepareAsync(context, ct);
        var fences = new List<CodeFence>();

        // The texts are not kept: a page is read again to write its results, so that the pages of the site are not
        // all in memory as strings at once.

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

            var index = 0;

            foreach (var (tagEnd, language, meta, code) in FindFences(text))
            {
                fences.Add(new CodeFence(i, ++index, tagEnd, language, meta, code, Hash128.Combine(settingsKey, Hash128.OfStrings([.. FenceKeyParts(language, meta, code)]))));
            }
        }

        if (fences.Count == 0)
        {
            return new StepOutput(FileList.Of(files));
        }

        // The outcomes: memoized ones, then the others, checked at once. An outcome with errors is memoized too: it is
        // the same for the same key, and reported again from the memo.
        var outcomes = new Dictionary<Hash128, (string? Json, string? Error)>();
        var cache = context.Build.Cache;
        var missing = new List<CodeFence>();
        var missingKeys = new HashSet<Hash128>();

        foreach (var fence in fences)
        {
            if (outcomes.ContainsKey(fence.Key) || missingKeys.Contains(fence.Key))
            {
                continue;
            }

            if (cache != null && cache.TryGetMemo(Name, fence.Key, out var memo))
            {
                outcomes[fence.Key] = ReadMemo(memo);
            }
            else
            {
                missing.Add(fence);
                missingKeys.Add(fence.Key);
            }
        }

        if (missing.Count > 0)
        {
            var checkedOutcomes = await CheckAsync(missing, state, context, ct);

            for (var i = 0; i < missing.Count; i++)
            {
                var (result, error) = checkedOutcomes[i];
                var outcome = (result?.ToJson(), error);
                outcomes[missing[i].Key] = outcome;
                cache?.PutMemo(Name, missing[i].Key, WriteMemo(outcome));
            }
        }

        // Every fence whose snippet is rejected is reported, by its page's source and its number on the page; with
        // errors: warn, it gets its result when there is one.
        var results = new Dictionary<Hash128, string>();
        var errors = new List<string>();

        foreach (var fence in fences)
        {
            var (json, error) = outcomes[fence.Key];

            if (error != null)
            {
                errors.Add($"{files[fence.File].SourcePath ?? files[fence.File].Path}: {FenceKind} fence {fence.Index} ('{FirstLine(fence.Code)}'): {error}");
            }

            if (json != null && (error == null || Warn))
            {
                results[fence.Key] = json;
            }
        }

        errors.Sort(StringComparer.Ordinal);

        if (errors.Count > 0 && !Warn)
        {
            throw new DouzeException(string.Join(Environment.NewLine, errors));
        }

        foreach (var error in errors)
        {
            context.Log(LogLevel.Warning, error);
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

            var text = await files[i].Content.ReadTextAsync(ct);
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

            // Waits for the other pages: as UTF-8.
            var content = FileContent.FromText(builder.ToString());
            content.Compact();
            outputs[i] = files[i].With(Name, content: content);
        }

        return new StepOutput(FileList.Of(outputs));
    }

    /// <summary>
    /// A memoized outcome: the result's JSON alone, or <c>!</c>, the error, a NUL and the JSON of the result if there
    /// is one.
    /// </summary>
    private static byte[] WriteMemo((string? Json, string? Error) outcome) => Encoding.UTF8.GetBytes(outcome.Error == null
        ? outcome.Json ?? ""
        : $"!{outcome.Error}\0{outcome.Json}");

    private static (string? Json, string? Error) ReadMemo(byte[] memo)
    {
        var text = Encoding.UTF8.GetString(memo);

        if (!text.StartsWith('!'))
        {
            return (text, null);
        }

        var separator = text.IndexOf('\0', StringComparison.Ordinal);
        var json = text[(separator + 1)..];
        return (json.Length == 0 ? null : json, text[1..separator]);
    }

    /// <summary>
    /// The full path of a path option, relative to the source folder.
    /// </summary>
    protected static string ResolvePath(BuildContext context, string path)
    {
        return Path.IsPathRooted(path) || context.Site.SourceDirectory is not { } source ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(source, path));
    }

    /// <summary>
    /// A file a path option names, such as the binary of a language server.
    /// </summary>
    protected static Requirement FileRequirement(string name, string path, string hint) => Requirement.Create(name, hint, _ =>
        ValueTask.FromResult(File.Exists(path) ? RequirementStatus.Found(path: path) : RequirementStatus.Missing($"does not exist at '{path}'")));

    /// <summary>
    /// An executable the step runs: the file of its option when set (relative to the source folder), else
    /// <paramref name="command"/> on <c>PATH</c>. Nothing is run: its version is not known.
    /// </summary>
    protected static Requirement ExecutableRequirement(BuildContext context, string command, string? option, string hint) => option != null
        ? FileRequirement(command, ResolvePath(context, option), hint)
        : Requirement.Create(command, hint, _ => ValueTask.FromResult(FindExecutable(context, command) is { } found
            ? RequirementStatus.Found(path: found)
            : RequirementStatus.Missing("is not on PATH")));

    /// <summary>
    /// The full path of <paramref name="command"/> on <c>PATH</c>, or null.
    /// </summary>
    protected static string? FindExecutable(BuildContext context, string command) =>
        Commands.CommandRequirements.FindOnPath(command, context.Site.SourceDirectory ?? Environment.CurrentDirectory);

    /// <summary>
    /// Reads the files of a <c>files</c> option (paths next to the snippets to files of the source folder), recording
    /// them as dependencies of the step.
    /// </summary>
    protected async Task<List<SharedFile>> ReadSharedFilesAsync(IEnumerable<KeyValuePair<string, FPath>> files, StepContext context)
    {
        var shared = new List<SharedFile>();

        foreach (var (path, source) in files)
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
    /// Reads a <c>files</c> option: a mapping of relative paths (without <c>.</c> or <c>..</c>) next to the snippets to
    /// files of the source folder.
    /// </summary>
    protected SortedDictionary<string, FPath> ReadFilesOption(Node? files, string option)
    {
        var result = new SortedDictionary<string, FPath>(StringComparer.Ordinal);

        if (files == null)
        {
            return result;
        }

        if (files is not NodeObject filesObject || filesObject.Any(x => x.Value is not NodeValue<string>))
        {
            throw Error($"option '{option}' of step '{Name}' must be a mapping of paths next to the snippets to files of the source folder");
        }

        foreach (var (path, source) in filesObject)
        {
            var relative = path.Replace('\\', '/');

            if (relative.Length == 0 || relative.StartsWith('/') || relative.Split('/').Any(x => x is ".." or "."))
            {
                throw Error($"option '{option}' of step '{Name}' has an invalid path '{path}': it must be relative, without '.' or '..'");
            }

            result[relative] = new FPath(((NodeValue<string>)source!).Value).ToRelative();
        }

        return result;
    }

    /// <summary>
    /// The first line of <c>--version</c> of a program, or <paramref name="fallback"/>.
    /// </summary>
    protected static async Task<string> VersionAsync(string executable, IEnumerable<string> arguments, string fallback, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Commands.ProcessRunner.Start(info, "");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output.Split('\n')[0].Trim() is { Length: > 0 } line ? line : fallback;
    }

    /// <summary>
    /// Deletes a work folder, unless <c>--verbose</c> asked to keep it. Never throws.
    /// </summary>
    protected static void DeleteWorkFolder(BuildContext build, string folder)
    {
        if (build.Options.Verbose)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FirstLine(string code)
    {
        var line = code.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 60 ? line[..57] + "..." : line;
    }

    [GeneratedRegex(@"\Gpre((?:\s+data-(?:lang|meta)=""[^""]*"")*)\s*>")]
    private static partial Regex PreTag();

    [GeneratedRegex(@"\G<code(?:\s[^>]*)?>")]
    private static partial Regex CodeTag();

    [GeneratedRegex(@"data-(lang|meta)=""([^""]*)""")]
    private static partial Regex PreAttribute();

    /// <summary>
    /// The fences of a page that the step selects, as the <c>markdown</c> step writes fences: a <c>&lt;pre&gt;</c> with
    /// only <c>data-lang</c> and <c>data-meta</c> attributes whose only child is a <c>&lt;code&gt;</c> holding escaped
    /// text. Returns where the <c>&lt;pre</c> tag's attributes end, the language, the meta and the decoded code (without
    /// the last line end, tabs replaced as <c>tab-width</c> says, trimmed as <c>trim</c> says).
    /// </summary>
    private IEnumerable<(int TagEnd, string Language, string Meta, string Code)> FindFences(string html)
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

            if (!_languages.Contains(language) || !Selects(meta) || text.Contains('<'))
            {
                continue;
            }

            var decoded = WebUtility.HtmlDecode(text.ToString()).ReplaceLineEndings("\n");

            if (decoded.EndsWith('\n'))
            {
                decoded = decoded[..^1];
            }

            if (TabWidth > 0)
            {
                decoded = decoded.Replace("\t", new string(' ', TabWidth), StringComparison.Ordinal);
            }

            if (Trim)
            {
                decoded = TrimCode(decoded);
            }

            // The end of the attributes: before the '>' of the tag, and its spaces.
            var tagEnd = pre.Index + pre.Length - 1;

            while (char.IsWhiteSpace(html[tagEnd - 1]))
            {
                tagEnd--;
            }

            yield return (tagEnd, language, meta, decoded);
        }
    }

    /// <summary>
    /// The code with the white space at the end of its lines and its leading and trailing blank lines removed.
    /// </summary>
    private static string TrimCode(string code)
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
}
