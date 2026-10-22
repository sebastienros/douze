using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow.Commands;

namespace Douze.CodeHover.Go;

/// <summary>
/// A Go snippet to check: its code (with <c>\n</c> line ends), the module it compiles in and the file name it has in
/// its package (and its errors give).
/// </summary>
internal sealed record GoSnippetRequest(string Code, GoModule Module, string FileName);

/// <summary>
/// Checks Go snippets with gopls, all at once: the snippets of a module are the packages of one generated module
/// (<see cref="GoModule"/>, each snippet in a folder of its own, <c>s&lt;n&gt;</c>, completed by
/// <see cref="GoSnippet"/>), whose requirements <c>go get</c> adds, then loaded by a gopls that is asked the semantic
/// tokens, the diagnostics and the hover of every symbol of each snippet, in parallel. The result of a snippet has the
/// hovers of its symbols (signature and documentation) and its errors, in twoslash's shape.
/// </summary>
internal sealed partial class GoChecker
{
    private readonly string _go;
    private readonly string _gopls;
    private readonly string _goVersion;
    private readonly string _workRoot;
    private readonly int _maxProcesses;
    private readonly TimeSpan _timeout;
    private readonly IReadOnlySet<string> _ignored;
    private readonly Action<LogLevel, string> _log;
    private readonly ITraceRecorder _trace;
    private readonly string _step;

    /// <param name="go">The <c>go</c> executable, which gopls runs too.</param>
    /// <param name="gopls">The gopls executable.</param>
    /// <param name="goVersion">The language version of the modules (<c>1.26.2</c>), or empty for none.</param>
    /// <param name="workRoot">A folder for the modules, which the caller deletes.</param>
    /// <param name="maxProcesses">How many gopls run at once.</param>
    /// <param name="timeout">How long a module may take, <c>go get</c> included.</param>
    /// <param name="ignored">The diagnostic codes that are neither reported nor rendered.</param>
    /// <param name="log">Where messages are reported.</param>
    /// <param name="trace">Where the <c>go get</c> runs are traced.</param>
    /// <param name="step">The step, in traces.</param>
    public GoChecker(string go, string gopls, string goVersion, string workRoot, int maxProcesses, TimeSpan timeout, IReadOnlySet<string> ignored, Action<LogLevel, string> log, ITraceRecorder trace, string step)
    {
        _go = go;
        _gopls = gopls;
        _goVersion = goVersion;
        _workRoot = workRoot;
        _maxProcesses = Math.Max(1, maxProcesses);
        _timeout = timeout;
        _ignored = ignored;
        _log = log;
        _trace = trace;
        _step = step;
    }

    /// <summary>
    /// The number of gopls processes the last check started.
    /// </summary>
    public int Processes { get; private set; }

    /// <summary>
    /// Checks <paramref name="snippets"/> and returns their outcomes in the same order.
    /// </summary>
    /// <exception cref="DouzeException">A <c>go get</c> or gopls failed: no outcome is known.</exception>
    public async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<GoSnippetRequest> snippets, CancellationToken ct)
    {
        var outcomes = new CodeOutcome?[snippets.Count];
        var groups = snippets.Select((snippet, index) => (snippet, index))
            .GroupBy(x => x.snippet.Module.Name, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        Processes = groups.Count;
        using var slots = new SemaphoreSlim(_maxProcesses, _maxProcesses);

        await Task.WhenAll(groups.Select((group, n) => Task.Run(async () =>
        {
            await slots.WaitAsync(ct);

            try
            {
                await CheckModuleAsync([.. group], Path.Combine(_workRoot, n.ToString(CultureInfo.InvariantCulture)), outcomes, ct);
            }
            finally
            {
                slots.Release();
            }
        }, ct)));

        return [.. outcomes.Select(x => x!)];
    }

    /// <summary>
    /// The folder of the <paramref name="n"/>th snippet's package, relative to its module.
    /// </summary>
    public static string PackageFolder(int n) => "s" + n.ToString(CultureInfo.InvariantCulture);

    private async Task CheckModuleAsync(List<(GoSnippetRequest Snippet, int Index)> group, string folder, CodeOutcome?[] outcomes, CancellationToken ct)
    {
        var module = group[0].Snippet.Module;
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "go.mod"), GoModule.ToModuleFile(_goVersion), ct);

        foreach (var shared in module.Files)
        {
            var path = Path.Combine(folder, shared.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, shared.Content, ct);
        }

        var wrapped = new GoSnippet[group.Count];
        var paths = new string[group.Count];

        for (var n = 0; n < group.Count; n++)
        {
            wrapped[n] = GoSnippet.Wrap(group[n].Snippet.Code);
            paths[n] = Path.Combine(folder, PackageFolder(n), group[n].Snippet.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(paths[n])!);
            await File.WriteAllTextAsync(paths[n], wrapped[n].Document, ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        if (module.Requires.Count > 0)
        {
            try
            {
                await GetAsync(module, folder, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DouzeException($"the requirements of the Go module '{module.Name}' were not added within {ProcessRunner.FormatDuration(_timeout)}");
            }
        }

        var info = new ProcessStartInfo(_gopls) { WorkingDirectory = folder };
        SetEnvironment(info);

        var server = LspClient.Start(info, "gopls");

        try
        {
            // Semantic tokens are off by default; links to pkg.go.dev in hovers are of no use for the snippets' own
            // symbols.
            await server.InitializeAsync(folder, "snippets", static writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("semanticTokens", true);
                writer.WriteBoolean("linksInHover", false);
                writer.WriteEndObject();
            }, timeout.Token);

            // gopls makes a snapshot of the workspace for every change, an open or a close included, and type-checks the
            // open packages of each: the snippets are opened at once, and stay open until gopls exits.
            var uris = paths.Select(x => new Uri(x).AbsoluteUri).ToArray();

            for (var n = 0; n < group.Count; n++)
            {
                await server.OpenAsync(uris[n], "go", wrapped[n].Document);
            }

            var results = await Task.WhenAll(group.Select((x, n) => CheckSnippetAsync(server, uris[n], wrapped[n], x.Snippet.FileName, timeout.Token)));

            for (var n = 0; n < group.Count; n++)
            {
                outcomes[group[n].Index] = results[n];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            server.Kill();
            throw new DouzeException($"gopls did not check {group.Count} Go snippet(s) within {ProcessRunner.FormatDuration(_timeout)}");
        }
        finally
        {
            await server.ShutdownAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Adds the module's requirements with <c>go get</c>, which also adds the requirements of the required modules,
    /// then the checksums of every module of the build list with <c>go mod download all</c>: <c>go get</c> only adds
    /// those that the root packages of the required modules need, and gopls cannot load a package of a module whose
    /// checksum is missing (<c>go.opentelemetry.io/otel/sdk/trace</c> rather than its root).
    /// </summary>
    private async Task GetAsync(GoModule module, string folder, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        string[][] commands = [["get", .. module.RequireArguments()], ["mod", "download", "all"]];

        foreach (var arguments in commands)
        {
            var (exitCode, output) = await GoAsync(module, folder, arguments, ct);

            if (exitCode != 0)
            {
                throw new DouzeException($"cannot add the requirements of the Go module '{module.Name}' (go {arguments[0]}): {ProcessRunner.Tail(output, 10)}");
            }
        }

        _log(LogLevel.Debug, $"added the requirements of the Go module '{module.Name}' in {ProcessRunner.FormatDuration(stopwatch.Elapsed)}");
    }

    private async Task<(int ExitCode, string Output)> GoAsync(GoModule module, string folder, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(_go)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        SetEnvironment(info);

        var result = await ProcessRunner.RunAsync(info, null, captureOutput: true, null, null, _trace, _step, $" (go {arguments.First()} of the Go module '{module.Name}')", ct);
        return (result.ExitCode, Encoding.UTF8.GetString(result.Stdout.Span) + Environment.NewLine + result.Stderr);
    }

    /// <summary>
    /// The environment of <c>go</c> and gopls: the generated module alone (no <c>go.work</c> of the folders above, no
    /// <c>GOFLAGS</c> meant for the site's own module such as <c>-mod=vendor</c>), and the <c>go</c> of the step first
    /// on the <c>PATH</c>, which gopls runs.
    /// </summary>
    public static void SetEnvironment(ProcessStartInfo info, string? go = null)
    {
        info.Environment["GOWORK"] = "off";
        info.Environment.Remove("GOFLAGS");

        if (go != null && Path.IsPathRooted(go) && Path.GetDirectoryName(go) is { } directory)
        {
            info.Environment["PATH"] = directory + Path.PathSeparator + (info.Environment.TryGetValue("PATH", out var path) ? path : "");
        }
    }

    private void SetEnvironment(ProcessStartInfo info) => SetEnvironment(info, _go);

    private async Task<CodeOutcome> CheckSnippetAsync(LspClient server, string uri, GoSnippet snippet, string fileName, CancellationToken ct)
    {
        var document = new PositionConverter(snippet.Document);
        var nodes = new List<TwoslashNode>();
        var errors = new List<TwoslashNode>();

        // The diagnostics first: gopls loads the package of a file again when it opens with an import it cannot
        // resolve, and answers other requests about the file with "no package data" until it is loaded.
        var diagnostics = await server.DiagnosticsAsync(uri, ct);
        var hovers = new List<(int Start, int Length, Task<string?> Markdown)>();

        foreach (var token in await server.SemanticTokensAsync(uri, ct))
        {
            if (!SemanticToken.IsSymbol(token.Type))
            {
                continue;
            }

            var start = snippet.ToCode(document.ToIndex(token.Line, token.Character));
            var end = snippet.ToCode(document.ToIndex(token.Line, token.Character + token.Length));

            // The blank identifier has nothing to show.
            if (start < 0 || end - start != token.Length || snippet.Code.AsSpan(start, token.Length) is "_")
            {
                continue;
            }

            hovers.Add((start, token.Length, HoverAsync(server, uri, token.Line, token.Character, ct)));
        }

        foreach (var (start, length, markdown) in hovers)
        {
            if (await markdown is { } text && ParseHover(text) is var (signature, docs))
            {
                nodes.Add(new TwoslashNode(TwoslashNodeType.Hover, start, length)
                {
                    Text = signature,
                    Docs = docs,
                    Target = snippet.Code.Substring(start, length),
                });
            }
        }

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity != 1 || _ignored.Contains(diagnostic.Code))
            {
                continue;
            }

            var start = snippet.ToCode(document.ToIndex(diagnostic.Start.Line, diagnostic.Start.Character), clamp: true);
            var end = Math.Max(start, snippet.ToCode(document.ToIndex(diagnostic.End.Line, diagnostic.End.Character), clamp: true));

            errors.Add(new TwoslashNode(TwoslashNodeType.Error, start, end - start)
            {
                CodeName = diagnostic.Code.Length > 0 ? diagnostic.Code : null,
                Filename = fileName,
                Text = diagnostic.Message,
                Level = "error",
            });
        }

        nodes.AddRange(errors);
        var result = TwoslashResult.Resolve(snippet.Code, nodes);

        if (errors.Count == 0)
        {
            return new CodeOutcome(result, null);
        }

        var details = string.Join("; ", errors.OrderBy(x => x.Start).Take(5).Select(x => $"{x.CodeName ?? "error"} on line {x.Line + 1}: {x.Text}"));
        return new CodeOutcome(result, $"{errors.Count} error(s): {details}{(errors.Count > 5 ? "; ..." : "")}");
    }

    /// <summary>
    /// The hover at a position, or null when gopls answers with an error, as it does for the name of a package whose
    /// import it cannot load.
    /// </summary>
    private static async Task<string?> HoverAsync(LspClient server, string uri, int line, int character, CancellationToken ct)
    {
        try
        {
            return await server.HoverAsync(uri, line, character, ct);
        }
        catch (LspErrorException)
        {
            return null;
        }
    }

    // gopls's comment of the size and offset of a type or field at its declaration.
    [GeneratedRegex(@"[ \t]*// size=\d+ \(0x[0-9a-f]+\)(?:, offset=\d+(?: \(0x[0-9a-f]+\))?)?$", RegexOptions.Multiline)]
    private static partial Regex SizeComment();

    // A doc link to a file of the Go sources: [Print](file:///.../print.go#271,6).
    [GeneratedRegex(@"\[([^\]\n]*)\]\(file://[^)\s]*\)")]
    private static partial Regex FileLink();

    // What gopls lists under a package clause: its path, module and language version.
    [GeneratedRegex(@"^ - (?:Package path|Module|Language version): .*(?:\n|$)", RegexOptions.Multiline)]
    private static partial Regex PackageDetails();

    /// <summary>
    /// The signature and documentation of a gopls hover, which is
    /// <code>
    /// ```go
    /// signature
    /// ```
    ///
    /// ---
    ///
    /// documentation (Markdown, its doc links to the Go sources as file: urls)
    /// </code>
    /// or null when it has no signature. The size comments of declarations and the links to files are dropped, and the
    /// documentation of a package is its first paragraph: the rest is the whole package's documentation.
    /// </summary>
    public static (string Signature, string? Docs)? ParseHover(string markdown)
    {
        markdown = markdown.ReplaceLineEndings("\n");
        var open = markdown.IndexOf("```", StringComparison.Ordinal);
        var first = open < 0 ? -1 : markdown.IndexOf('\n', open);

        if (first < 0)
        {
            return null;
        }

        var close = markdown.IndexOf("\n```", first, StringComparison.Ordinal);

        if (close < 0)
        {
            return null;
        }

        var signature = SizeComment().Replace(markdown[(first + 1)..close], "").Trim();

        if (signature.Length == 0)
        {
            return null;
        }

        var rest = markdown[(close + 4)..].TrimStart();

        if (rest.StartsWith("---", StringComparison.Ordinal))
        {
            rest = rest[3..];
        }

        rest = PackageDetails().Replace(FileLink().Replace(rest, "$1"), "");
        var docs = string.Join('\n', rest.Split('\n').Select(x => x.TrimEnd())).Trim();

        if (signature.StartsWith("package ", StringComparison.Ordinal) && docs.IndexOf("\n\n", StringComparison.Ordinal) is var paragraph and >= 0)
        {
            docs = docs[..paragraph];
        }

        return (signature, docs.Length == 0 ? null : docs);
    }
}
