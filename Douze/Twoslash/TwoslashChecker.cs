using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Douze.CodeHover;
using Douze.Progress;

namespace Douze.Twoslash;

/// <summary>
/// A snippet to check: its code (with <c>\n</c> line ends) and its fence language.
/// </summary>
internal sealed record TwoslashSnippet(string Code, string Language);

/// <summary>
/// What every snippet of a check shares: the compiler options (tsconfig names to JSON values) under the snippets' own,
/// the shared files, the names of the custom tags and the handbook options under the snippets' own.
/// </summary>
internal sealed record TwoslashSettings(
    IReadOnlyDictionary<string, string> CompilerOptions,
    IReadOnlyList<SharedFile> Files,
    IReadOnlyCollection<string> CustomTags,
    HandbookOptions Handbook)
{
    /// <summary>
    /// twoslash's default compiler options, in the tsconfig form.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultCompilerOptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["strict"] = "true",
        ["module"] = "\"esnext\"",
        ["target"] = "\"esnext\"",
        ["allowJs"] = "true",
        ["skipDefaultLibCheck"] = "true",
        ["skipLibCheck"] = "true",
        ["moduleDetection"] = "\"force\"",
    };
}

/// <summary>
/// Checks snippets the way twoslash does, with a TypeScript 7 language server (<c>tsc --lsp --stdio</c> of TypeScript
/// 7, or tsrs, which speaks the same protocol) instead of the TypeScript compiler API, all at once: the snippets that
/// share their compiler options are the files of one project, each in its own folder (<c>&lt;n&gt;/index.ts</c>, with
/// the files of <c>// @filename:</c>), and the shared files at the root, which <c>rootDirs</c> merges with every
/// snippet's folder so that <c>./x</c> finds them as it would next to the snippet. Snippets are modules
/// (<c>moduleDetection: force</c>, twoslash's default), so they do not see each other's declarations; one that augments
/// the global scope or declares a module gets a project of its own. Each project is a language server process: its
/// hover, completion and diagnostic requests are sent at once and answered in parallel.
/// </summary>
internal sealed partial class TwoslashChecker
{
    // Words that are keywords where an identifier is not expected, with quick info of their own (`this`, the
    // constructor of `new X`), unless they follow a dot.
    private static readonly HashSet<string> SkippedWords = new(StringComparer.Ordinal) { "this", "super", "new", "true", "false", "null", "constructor", "import" };

    [GeneratedRegex(@"\bdeclare\s+(global|module)\b")]
    private static partial Regex GlobalDeclaration();

    private static readonly System.Buffers.SearchValues<char> WordCharacters = System.Buffers.SearchValues.Create("$_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    [GeneratedRegex(@"[$\w]+$", RegexOptions.ECMAScript)]
    private static partial Regex CompletionPrefix();

    private readonly string _executable;
    private readonly TwoslashSettings _settings;
    private readonly string _workRoot;
    private readonly int _maxProcesses;
    private readonly TimeSpan _timeout;
    private readonly Action<LogLevel, string> _log;

    /// <param name="executable">The path of the language server's binary (<c>tsc</c> or <c>tsrs</c>).</param>
    /// <param name="settings">What every snippet shares.</param>
    /// <param name="workRoot">A folder for the projects, which the caller deletes.</param>
    /// <param name="maxProcesses">How many language servers run at once.</param>
    /// <param name="timeout">How long a project may take.</param>
    /// <param name="log">Where messages are reported.</param>
    public TwoslashChecker(string executable, TwoslashSettings settings, string workRoot, int maxProcesses, TimeSpan timeout, Action<LogLevel, string> log)
    {
        _executable = executable;
        _settings = settings;
        _workRoot = workRoot;
        _maxProcesses = Math.Max(1, maxProcesses);
        _timeout = timeout;
        _log = log;
    }

    /// <summary>
    /// The number of language server processes the last check started.
    /// </summary>
    public int Processes { get; private set; }

    /// <summary>
    /// Checks <paramref name="snippets"/> and returns their outcomes in the same order.
    /// </summary>
    /// <exception cref="DouzeException">A language server failed: no outcome is known.</exception>
    public async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<TwoslashSnippet> snippets, CancellationToken ct)
    {
        var outcomes = new CodeOutcome?[snippets.Count];
        var groups = new SortedDictionary<string, List<Prepared>>(StringComparer.Ordinal);

        for (var i = 0; i < snippets.Count; i++)
        {
            try
            {
                var prepared = Prepare(i, snippets[i]);
                var key = prepared.OptionsKey + (GlobalDeclaration().IsMatch(snippets[i].Code) ? "\0" + i.ToString(CultureInfo.InvariantCulture) : "");

                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = [];
                }

                group.Add(prepared);
            }
            catch (TwoslashException e)
            {
                outcomes[i] = new CodeOutcome(null, e.Message);
            }
        }

        Processes = groups.Count;
        using var slots = new SemaphoreSlim(_maxProcesses, _maxProcesses);
        var groupIndex = 0;

        await Task.WhenAll(groups.Values.Select(group =>
        {
            var folder = Path.Combine(_workRoot, (groupIndex++).ToString(CultureInfo.InvariantCulture));

            return Task.Run(async () =>
            {
                await slots.WaitAsync(ct);

                try
                {
                    await CheckGroupAsync(group, folder, outcomes, ct);
                }
                finally
                {
                    slots.Release();
                }
            }, ct);
        }));

        return [.. outcomes.Select(x => x!)];
    }

    /// <summary>
    /// A snippet with its notations read and its effective compiler options.
    /// </summary>
    private sealed record Prepared(int Index, TwoslashSnippet Snippet, TwoslashMarkup Markup, SortedDictionary<string, string> Options)
    {
        public string OptionsKey => string.Join("\n", Options.Select(x => $"{x.Key}={x.Value}"));
    }

    private Prepared Prepare(int index, TwoslashSnippet snippet)
    {
        var markup = TwoslashMarkup.Parse(snippet.Code, snippet.Language, _settings.CustomTags, _settings.Handbook);

        if (markup.Handbook.ShowEmit)
        {
            throw new TwoslashException("'showEmit' is not supported");
        }

        if (!markup.Handbook.NoErrorValidation && markup.UnknownFlags.Count > 0)
        {
            throw new TwoslashException($"unknown inline compiler flags: {string.Join(", ", markup.UnknownFlags.Select(x => "@" + x))}");
        }

        var options = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in _settings.CompilerOptions)
        {
            options[name] = value;
        }

        foreach (var (name, value) in markup.CompilerOptions)
        {
            options[name] = value;
        }

        foreach (var file in markup.Files)
        {
            if (file.Name.Length == 0 || Path.IsPathRooted(file.Name) || file.Name.Split('/', '\\').Any(x => x is ".." or "."))
            {
                throw new TwoslashException($"invalid file name '{file.Name}'");
            }
        }

        return new Prepared(index, snippet, markup, options);
    }

    private async Task CheckGroupAsync(List<Prepared> group, string folder, CodeOutcome?[] outcomes, CancellationToken ct)
    {
        var resolveJson = group[0].Options.TryGetValue("resolveJsonModule", out var resolve) && resolve == "true";
        var checkedFiles = new List<string>();

        // The project: the shared files at the root, each snippet's files in its folder.
        Directory.CreateDirectory(folder);

        foreach (var shared in _settings.Files)
        {
            var path = Path.Combine(folder, shared.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, shared.Content, ct);
        }

        for (var n = 0; n < group.Count; n++)
        {
            foreach (var file in group[n].Markup.Files)
            {
                var relative = $"{n}/{file.Name}";
                var path = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Content, ct);

                if (file.IsChecked(resolveJson))
                {
                    checkedFiles.Add(relative);
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "tsconfig.json"), TsConfig(group[0].Options, group.Count, checkedFiles), ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        var server = LspClient.Start(new ProcessStartInfo(_executable) { ArgumentList = { "--lsp", "--stdio" }, WorkingDirectory = folder }, Path.GetFileNameWithoutExtension(_executable));

        try
        {
            await server.InitializeAsync(folder, "twoslash", null, timeout.Token);
            var results = await Task.WhenAll(group.Select((prepared, n) => CheckSnippetAsync(server, folder, n, prepared, timeout.Token)));

            for (var n = 0; n < group.Count; n++)
            {
                outcomes[group[n].Index] = results[n];
            }

            ThrowOnConfigurationErrors(server, folder);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            server.Kill();
            throw new DouzeException($"the language server did not check {group.Count} snippet(s) within {Workflow.Commands.ProcessRunner.FormatDuration(_timeout)}");
        }
        finally
        {
            await server.ShutdownAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string TsConfig(SortedDictionary<string, string> options, int snippets, List<string> files)
    {
        var json = new StringBuilder("{\n  \"compilerOptions\": {\n");

        foreach (var (name, value) in options)
        {
            if (name is "rootDirs" or "noEmit")
            {
                continue;
            }

            json.Append("    ").Append(TwoslashMarkup.JsonString(name)).Append(": ").Append(value).Append(",\n");
        }

        json.Append("    \"noEmit\": true,\n    \"rootDirs\": ");
        json.Append(TwoslashMarkup.JsonArray(Enumerable.Range(0, snippets).Select(x => x.ToString(CultureInfo.InvariantCulture)).Prepend(".")));
        json.Append("\n  },\n  \"files\": ").Append(TwoslashMarkup.JsonArray(files)).Append("\n}\n");
        return json.ToString();
    }

    /// <summary>
    /// Fails the project when the server reported errors in its tsconfig (an invalid compiler option value).
    /// </summary>
    private static void ThrowOnConfigurationErrors(LspClient server, string folder)
    {
        foreach (var (uri, published) in server.Connection.PushedDiagnostics)
        {
            if (!uri.EndsWith("/tsconfig.json", StringComparison.Ordinal) || !published.TryGetProperty("diagnostics", out var items))
            {
                continue;
            }

            var errors = items.EnumerateArray()
                .Where(x => x.TryGetProperty("severity", out var s) && s.GetInt32() == 1)
                .Select(x => x.GetProperty("message").GetString())
                .ToList();

            if (errors.Count > 0)
            {
                throw new DouzeException($"invalid compiler options: {string.Join("; ", errors)}");
            }
        }
    }

    private async Task<CodeOutcome> CheckSnippetAsync(LspClient server, string folder, int n, Prepared prepared, CancellationToken ct)
    {
        var markup = prepared.Markup;
        var handbook = markup.Handbook;
        var resolveJson = prepared.Options.TryGetValue("resolveJsonModule", out var resolve) && resolve == "true";
        var files = markup.Files.Where(x => x.IsChecked(resolveJson)).Select(file => new CheckedFile(
            file,
            new Uri(Path.Combine(folder, n.ToString(CultureInfo.InvariantCulture), file.Name)).AbsoluteUri,
            new PositionConverter(file.Content),
            IdentifierScanner.Scan(file.Content, file.Extension is "tsx" or "jsx"))).ToList();

        // The requests, all sent at once: hovers, queries, completions and diagnostics.
        var hovers = new List<(CheckedFile File, IdentifierScanner.Word Word, Task<QuickInfo?> Info)>();

        if (!handbook.NoStaticSemanticInfo)
        {
            foreach (var file in files)
            {
                foreach (var word in file.Words)
                {
                    var start = file.File.Offset + word.Start;

                    if (markup.IsInRemoval(start) || (!word.AfterDot && SkippedWords.Contains(file.File.Content.Substring(word.Start, word.Length))))
                    {
                        continue;
                    }

                    hovers.Add((file, word, HoverAsync(server, file, word, ct)));
                }
            }
        }

        var queries = new List<(int Position, CheckedFile? File, IdentifierScanner.Word? Word, Task<QuickInfo?>? Info)>();

        foreach (var query in markup.Queries)
        {
            if (markup.IsInRemoval(query))
            {
                return Failure($"the quick info query (^?) on line {LineOf(markup.Code, query) + 2} is in a removed range");
            }

            var file = FileAt(files, query);
            var word = file?.Words.Cast<IdentifierScanner.Word?>().FirstOrDefault(x => file.File.Offset + x!.Value.Start <= query && query <= file.File.Offset + x.Value.Start + x.Value.Length);
            queries.Add((query, file, word, file != null && word != null ? HoverAsync(server, file, word.Value, ct) : null));
        }

        var completions = new List<(int Position, Task<(List<CompletionEntry> Entries, string Prefix)> Entries)>();

        foreach (var target in markup.Completions)
        {
            var file = FileAt(files, target);

            if (markup.IsInRemoval(target) || file == null)
            {
                return Failure($"the completion query (^|) on line {LineOf(markup.Code, target) + 2} is in a removed range");
            }

            completions.Add((target, CompletionsAsync(server, file, markup.Code, target, ct)));
        }

        var diagnostics = handbook.NoErrors ? [] : files.Select(file => (file, DiagnosticsAsync(server, file, ct))).ToList();

        // The nodes, in the order twoslash creates them: tags, hovers, queries, highlights, completions, errors.
        var nodes = new List<TwoslashNode>(markup.Tags);

        foreach (var (file, word, info) in hovers)
        {
            if (await info is { } quickInfo)
            {
                nodes.Add(HoverNode(TwoslashNodeType.Hover, file, word, quickInfo));
            }
        }

        foreach (var (position, file, word, info) in queries)
        {
            if (info == null || await info is not { } quickInfo)
            {
                return Failure($"the quick info query (^?) on line {LineOf(markup.Code, position) + 2} found nothing");
            }

            nodes.Add(HoverNode(TwoslashNodeType.Query, file!, word!.Value, quickInfo));
        }

        foreach (var (start, end, text) in markup.Highlights)
        {
            nodes.Add(new TwoslashNode(TwoslashNodeType.Highlight, start, end - start) { Text = text });
        }

        foreach (var (position, task) in completions)
        {
            var (entries, prefix) = await task;

            if (entries.Count == 0 && !handbook.NoErrorValidation)
            {
                return Failure($"the completion query (^|) on line {LineOf(markup.Code, position)} found no completions (prefix: {prefix})");
            }

            nodes.Add(new TwoslashNode(TwoslashNodeType.Completion, position, 0) { Completions = entries, CompletionsPrefix = prefix });
        }

        var errors = new List<TwoslashNode>();

        foreach (var (file, task) in diagnostics)
        {
            foreach (var error in await task)
            {
                if (handbook.IgnoredErrors.Contains(error.Code) || (handbook.NoErrorsCutted && markup.IsInRemoval(error.Start)))
                {
                    continue;
                }

                errors.Add(error);
            }
        }

        nodes.AddRange(errors);

        if (!handbook.NoErrorValidation && errors.Any(x => !handbook.Errors.Contains(x.Code)))
        {
            var unexpected = errors.Where(x => !handbook.Errors.Contains(x.Code)).ToList();
            var expected = $"// @errors: {string.Join(' ', errors.Select(x => x.Code).Distinct())}";
            var details = string.Join("; ", unexpected.Select(x => $"[{x.Code}] {x.Filename}:{x.Start} - {x.Text}"));
            // The result is kept: a step that only warns renders the errors.
            return new CodeOutcome(Finish(markup, nodes), $"errors were thrown in the sample, but not included in an error tag: {string.Join(' ', unexpected.Select(x => x.Code).Distinct())}. "
                + (handbook.Errors.Count > 0 ? $"The existing annotation specified {string.Join(' ', handbook.Errors)}" : $"Expected: {expected}") + $". Compiler errors: {details}");
        }

        return new CodeOutcome(Finish(markup, nodes), null);

        static CodeOutcome Failure(string message) => new(null, message);
    }

    private sealed record CheckedFile(SnippetFile File, string Uri, PositionConverter Positions, List<IdentifierScanner.Word> Words);

    private static CheckedFile? FileAt(List<CheckedFile> files, int position) =>
        files.FirstOrDefault(x => x.File.Offset <= position && position <= x.File.Offset + x.File.Content.Length);

    private static int LineOf(string code, int index) => code.AsSpan(0, Math.Clamp(index, 0, code.Length)).Count('\n');

    private static TwoslashNode HoverNode(TwoslashNodeType type, CheckedFile file, IdentifierScanner.Word word, QuickInfo info)
    {
        return new TwoslashNode(type, file.File.Offset + word.Start, word.Length)
        {
            Text = info.Text,
            Docs = info.Docs,
            Tags = info.Tags,
            Target = file.File.Content.Substring(word.Start, word.Length),
        };
    }

    private static async Task<QuickInfo?> HoverAsync(LspClient server, CheckedFile file, IdentifierScanner.Word word, CancellationToken ct)
    {
        var (line, character) = file.Positions.ToPosition(word.Start);
        return await server.HoverAsync(file.Uri, line, character, ct) is { } markdown ? QuickInfo.Parse(markdown) : null;
    }

    private static async Task<List<TwoslashNode>> DiagnosticsAsync(LspClient server, CheckedFile file, CancellationToken ct)
    {
        var errors = new List<TwoslashNode>();

        foreach (var diagnostic in await server.DiagnosticsAsync(file.Uri, ct))
        {
            // Suggestions (hints) are not part of the semantic and syntactic diagnostics twoslash reports.
            if (diagnostic.Severity is < 1 or > 3)
            {
                continue;
            }

            var start = ToIndex(file.Positions, diagnostic.Start);
            var end = ToIndex(file.Positions, diagnostic.End);

            errors.Add(new TwoslashNode(TwoslashNodeType.Error, file.File.Offset + start, end - start)
            {
                Code = int.TryParse(diagnostic.Code, CultureInfo.InvariantCulture, out var code) ? code : 0,
                Filename = file.File.Name,
                Text = diagnostic.Message,
                Level = diagnostic.Severity switch { 1 => "error", 2 => "warning", _ => "message" },
            });
        }

        return errors;
    }

    private static async Task<(List<CompletionEntry>, string)> CompletionsAsync(LspClient server, CheckedFile file, string code, int target, CancellationToken ct)
    {
        var prefix = CompletionPrefix().Match(code[..target]) is { Success: true } match ? match.Value : "";
        var offset = target - file.File.Offset;
        List<CompletionEntry> entries;
        int? replacementStart;

        if (prefix.Length > 0)
        {
            // At the end of the word (twoslash asks TypeScript one character before, which a language server answers
            // with the completions of the word's start).
            var (line, character) = file.Positions.ToPosition(offset);
            (entries, replacementStart) = await CompletionRequestAsync(line, character, null);

            if (replacementStart is { } replacement && replacement < target && code.AsSpan(replacement, target - replacement).ContainsAnyExcept(WordCharacters) == false)
            {
                prefix = code[replacement..target];
            }

            entries = [.. entries.Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal))];
        }
        else
        {
            prefix = target > 0 ? code[target - 1].ToString() : "";
            entries = [];

            if (prefix.Length > 0)
            {
                var (line, character) = file.Positions.ToPosition(offset);
                (entries, replacementStart) = await CompletionRequestAsync(line, character, prefix);

                if (replacementStart is { } replacement && replacement < target)
                {
                    prefix = code[replacement..target];
                    var filtered = entries.Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

                    if (filtered.Count > 0)
                    {
                        entries = filtered;
                    }
                }
            }
        }

        return (entries, prefix);

        async Task<(List<CompletionEntry>, int?)> CompletionRequestAsync(int line, int character, string? trigger)
        {
            var result = await server.RequestAsync("textDocument/completion", writer => LspClient.WritePosition(writer, file.Uri, line, character, trigger), ct);
            var items = result.ValueKind == JsonValueKind.Array ? result
                : result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var list) ? list : default;
            var found = new List<CompletionEntry>();
            int? start = null;

            if (items.ValueKind != JsonValueKind.Array)
            {
                return (found, start);
            }

            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("label").GetString() ?? "";
                var kind = item.TryGetProperty("kind", out var k) ? CompletionKind(k.GetInt32()) : "";
                var deprecated = item.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(x => x.GetInt32() == 1);
                var sortText = item.TryGetProperty("sortText", out var sort) ? sort.GetString() : null;
                found.Add(new CompletionEntry(name, kind, deprecated ? "deprecated" : null, sortText));

                if (start == null && item.TryGetProperty("textEdit", out var edit))
                {
                    var range = edit.TryGetProperty("range", out var r) ? r : edit.TryGetProperty("replace", out var replace) ? replace : default;

                    if (range.ValueKind == JsonValueKind.Object)
                    {
                        start = file.File.Offset + ToIndex(file.Positions, LspClient.ReadPosition(range.GetProperty("start")));
                    }
                }
            }

            return (found, start);
        }
    }

    /// <summary>
    /// TypeScript's element kind of a completion item kind of the protocol.
    /// </summary>
    private static string CompletionKind(int kind) => kind switch
    {
        2 => "method",
        3 => "function",
        4 => "constructor",
        5 or 10 => "property",
        6 => "var",
        7 => "class",
        8 => "interface",
        9 => "module",
        13 => "enum",
        14 => "keyword",
        17 => "script",
        20 => "enum member",
        21 => "const",
        25 => "type parameter",
        _ => "",
    };

    private static int ToIndex(PositionConverter positions, (int Line, int Character) position) => positions.ToIndex(position.Line, position.Character);

    /// <summary>
    /// Removes the notations from the code and moves the nodes with it (twoslash's <c>removeCodeRanges</c>), then sorts
    /// the nodes by offset and type and gives them their line and character (<c>resolveNodePositions</c>).
    /// </summary>
    private static TwoslashResult Finish(TwoslashMarkup markup, List<TwoslashNode> nodes)
    {
        var code = markup.Code;

        if (!markup.Handbook.KeepNotations)
        {
            var merged = new List<(int Start, int End)>();

            foreach (var range in markup.Removals.OrderBy(x => x.Start))
            {
                if (merged.Count > 0 && merged[^1].End >= range.Start)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
                }
                else
                {
                    merged.Add(range);
                }
            }

            foreach (var (start, end) in merged.OrderByDescending(x => x.Start))
            {
                var removedEnd = Math.Min(end, code.Length);

                if (start < code.Length)
                {
                    code = string.Concat(code.AsSpan(0, start), code.AsSpan(removedEnd));
                }

                foreach (var node in nodes)
                {
                    if (node.Start + node.Length <= start)
                    {
                        continue;
                    }

                    if (node.Start < end)
                    {
                        node.Start = -1;
                    }
                    else
                    {
                        node.Start -= end - start;
                    }
                }
            }
        }

        return TwoslashResult.Resolve(code, nodes);
    }
}
