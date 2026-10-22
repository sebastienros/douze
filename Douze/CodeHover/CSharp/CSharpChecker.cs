using System.Diagnostics;
using System.Globalization;
using System.Text;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow.Commands;

namespace Douze.CodeHover.CSharp;

/// <summary>
/// A C# snippet to check: its code (with <c>\n</c> line ends), the project it compiles in and the file name its errors
/// give.
/// </summary>
internal sealed record CSharpSnippetRequest(string Code, CSharpProject Project, string FileName);

/// <summary>
/// Checks C# snippets with the Roslyn language server, all at once: the snippets of a project are the files of one
/// generated project (<see cref="CSharpProject"/>, each snippet wrapped in a namespace of its own:
/// <see cref="CSharpSnippet"/>), restored with <c>dotnet restore</c>, then loaded by a language server that is asked
/// the semantic tokens, the diagnostics and the hover of every symbol of each snippet, in parallel. The result of a
/// snippet has the hovers of its symbols (signature and documentation) and its errors, in twoslash's shape.
/// </summary>
internal sealed class CSharpChecker
{
    // The snippets open in a language server at once: an open document keeps its compilation state.
    private const int OpenDocuments = 64;

    private readonly string _dotnet;
    private readonly string _server;
    private readonly string _workRoot;
    private readonly int _maxProcesses;
    private readonly TimeSpan _timeout;
    private readonly IReadOnlySet<string> _ignored;
    private readonly Action<LogLevel, string> _log;
    private readonly ITraceRecorder _trace;
    private readonly string _step;

    /// <param name="dotnet">The <c>dotnet</c> executable (of a .NET SDK).</param>
    /// <param name="server">The language server's entry point, <c>Microsoft.CodeAnalysis.LanguageServer.dll</c>.</param>
    /// <param name="workRoot">A folder for the projects, which the caller deletes.</param>
    /// <param name="maxProcesses">How many language servers run at once.</param>
    /// <param name="timeout">How long a project may take, restore included.</param>
    /// <param name="ignored">The diagnostic codes that are neither reported nor rendered.</param>
    /// <param name="log">Where messages are reported.</param>
    /// <param name="trace">Where the restores are traced.</param>
    /// <param name="step">The step, in traces.</param>
    public CSharpChecker(string dotnet, string server, string workRoot, int maxProcesses, TimeSpan timeout, IReadOnlySet<string> ignored, Action<LogLevel, string> log, ITraceRecorder trace, string step)
    {
        _dotnet = dotnet;
        _server = server;
        _workRoot = workRoot;
        _maxProcesses = Math.Max(1, maxProcesses);
        _timeout = timeout;
        _ignored = ignored;
        _log = log;
        _trace = trace;
        _step = step;
    }

    /// <summary>
    /// The number of language server processes the last check started.
    /// </summary>
    public int Processes { get; private set; }

    /// <summary>
    /// Checks <paramref name="snippets"/> and returns their outcomes in the same order.
    /// </summary>
    /// <exception cref="DouzeException">A restore or a language server failed: no outcome is known.</exception>
    public async Task<IReadOnlyList<CodeOutcome>> CheckAsync(IReadOnlyList<CSharpSnippetRequest> snippets, CancellationToken ct)
    {
        var outcomes = new CodeOutcome?[snippets.Count];
        var groups = snippets.Select((snippet, index) => (snippet, index))
            .GroupBy(x => x.snippet.Project.Name, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        Processes = groups.Count;
        using var slots = new SemaphoreSlim(_maxProcesses, _maxProcesses);

        await Task.WhenAll(groups.Select((group, n) => Task.Run(async () =>
        {
            await slots.WaitAsync(ct);

            try
            {
                await CheckProjectAsync([.. group], Path.Combine(_workRoot, n.ToString(CultureInfo.InvariantCulture)), outcomes, ct);
            }
            finally
            {
                slots.Release();
            }
        }, ct)));

        return [.. outcomes.Select(x => x!)];
    }

    private async Task CheckProjectAsync(List<(CSharpSnippetRequest Snippet, int Index)> group, string folder, CodeOutcome?[] outcomes, CancellationToken ct)
    {
        var project = group[0].Snippet.Project;
        Directory.CreateDirectory(folder);

        // Nothing of the folders above (the site's own build files) changes the project.
        await File.WriteAllTextAsync(Path.Combine(folder, "Directory.Build.props"), "<Project />\n", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "Directory.Build.targets"), "<Project />\n", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "Directory.Packages.props"), "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "snippets.csproj"), project.ToProjectFile(), ct);

        foreach (var shared in project.Files)
        {
            var path = Path.Combine(folder, shared.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, shared.Content, ct);
        }

        var wrapped = new CSharpSnippet[group.Count];

        for (var n = 0; n < group.Count; n++)
        {
            wrapped[n] = CSharpSnippet.Wrap(group[n].Snippet.Code, n, project.Context);
            await File.WriteAllTextAsync(Path.Combine(folder, DocumentName(n)), wrapped[n].Document, ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        try
        {
            await RestoreAsync(project, folder, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DouzeException($"the C# project '{project.Name}' was not restored within {ProcessRunner.FormatDuration(_timeout)}");
        }

        var info = new ProcessStartInfo(_dotnet)
        {
            ArgumentList = { _server, "--stdio", "--autoLoadProjects", "--telemetryLevel", "off", "--logLevel", "Warning", "--extensionLogDirectory", Path.Combine(folder, ".logs") },
            WorkingDirectory = folder,
        };
        SetEnvironment(info);

        // Server GC on every core costs gigabytes for no speed past a few heaps.
        if (Environment.GetEnvironmentVariable("DOTNET_GCHeapCount") == null)
        {
            info.Environment["DOTNET_GCHeapCount"] = Math.Min(4, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
        }

        var server = LspClient.Start(info, "roslyn");

        try
        {
            await server.InitializeAsync(folder, "snippets", null, timeout.Token);
            await server.Connection.WaitForNotificationAsync("workspace/projectInitializationComplete", timeout.Token);

            using var open = new SemaphoreSlim(OpenDocuments, OpenDocuments);
            var results = await Task.WhenAll(group.Select((x, n) => CheckSnippetAsync(server, open, folder, n, wrapped[n], x.Snippet.FileName, timeout.Token)));

            for (var n = 0; n < group.Count; n++)
            {
                outcomes[group[n].Index] = results[n];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            server.Kill();
            throw new DouzeException($"the language server did not check {group.Count} C# snippet(s) within {ProcessRunner.FormatDuration(_timeout)}");
        }
        finally
        {
            await server.ShutdownAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string DocumentName(int n) => $"s{n.ToString(CultureInfo.InvariantCulture)}.cs";

    private async Task RestoreAsync(CSharpProject project, string folder, CancellationToken ct)
    {
        var info = new ProcessStartInfo(_dotnet)
        {
            ArgumentList = { "restore", Path.Combine(folder, "snippets.csproj"), "--nologo", "--verbosity", "minimal" },
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        SetEnvironment(info);

        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(info, null, captureOutput: true, null, null, _trace, _step, $" (restore of the C# project '{project.Name}')", ct);

        if (result.ExitCode != 0)
        {
            var output = Encoding.UTF8.GetString(result.Stdout.Span) + Environment.NewLine + result.Stderr;
            throw new DouzeException($"cannot restore the C# project '{project.Name}': {ProcessRunner.Tail(output, 10)}");
        }

        _log(LogLevel.Debug, $"restored the C# project '{project.Name}' in {ProcessRunner.FormatDuration(stopwatch.Elapsed)}");
    }

    private static void SetEnvironment(ProcessStartInfo info)
    {
        // A Douze started by MSBuild (dotnet run, dotnet test) passes on the variables of its MSBuild, which break the
        // restore of another SDK's MSBuild.
        foreach (var name in info.Environment.Keys.Where(x => x.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) || x.StartsWith("_MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
    }

    private async Task<CodeOutcome> CheckSnippetAsync(LspClient server, SemaphoreSlim open, string folder, int n, CSharpSnippet snippet, string fileName, CancellationToken ct)
    {
        var uri = new Uri(Path.Combine(folder, DocumentName(n))).AbsoluteUri;
        var document = new PositionConverter(snippet.Document);
        var nodes = new List<TwoslashNode>();
        var errors = new List<TwoslashNode>();

        await open.WaitAsync(ct);

        try
        {
            // Pulled diagnostics are only computed for open documents.
            await server.OpenAsync(uri, "csharp", snippet.Document);

            var diagnostics = server.DiagnosticsAsync(uri, ct);
            var hovers = new List<(int Start, int Length, Task<string?> Markdown)>();

            foreach (var token in await server.SemanticTokensAsync(uri, ct))
            {
                if (!SemanticToken.IsSymbol(token.Type))
                {
                    continue;
                }

                var start = snippet.ToCode(document.ToIndex(token.Line, token.Character));
                var end = snippet.ToCode(document.ToIndex(token.Line, token.Character + token.Length));

                if (start < 0 || end - start != token.Length)
                {
                    continue;
                }

                hovers.Add((start, token.Length, server.HoverAsync(uri, token.Line, token.Character, ct)));
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

            foreach (var diagnostic in await diagnostics)
            {
                if (diagnostic.Severity != 1 || _ignored.Contains(diagnostic.Code))
                {
                    continue;
                }

                var start = snippet.ToCode(document.ToIndex(diagnostic.Start.Line, diagnostic.Start.Character), clamp: true);
                var end = Math.Max(start, snippet.ToCode(document.ToIndex(diagnostic.End.Line, diagnostic.End.Character), clamp: true));

                errors.Add(new TwoslashNode(TwoslashNodeType.Error, start, end - start)
                {
                    Code = NumberOf(diagnostic.Code),
                    CodeName = diagnostic.Code,
                    Filename = fileName,
                    Text = CSharpSnippet.Unwrap(diagnostic.Message),
                    Level = "error",
                });
            }
        }
        finally
        {
            await server.CloseAsync(uri);
            open.Release();
        }

        nodes.AddRange(errors);
        var result = TwoslashResult.Resolve(snippet.Code, nodes);

        if (errors.Count == 0)
        {
            return new CodeOutcome(result, null);
        }

        var details = string.Join("; ", errors.OrderBy(x => x.Start).Take(5).Select(x => $"{x.CodeName} on line {x.Line + 1}: {x.Text}"));
        return new CodeOutcome(result, $"{errors.Count} error(s): {details}{(errors.Count > 5 ? "; ..." : "")}");
    }

    /// <summary>
    /// The number of a diagnostic code (<c>CS1061</c> is 1061), for renderers that expect one.
    /// </summary>
    private static int NumberOf(string code)
    {
        var digits = code.Length;

        while (digits > 0 && char.IsAsciiDigit(code[digits - 1]))
        {
            digits--;
        }

        return int.TryParse(code.AsSpan(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    /// <summary>
    /// The signature and documentation of a Roslyn hover, which is
    /// <code>
    /// ```csharp
    /// signature
    /// ```
    ///
    /// documentation (Markdown, with hard line breaks and &amp;nbsp;)
    /// </code>
    /// or null when it has no signature.
    /// </summary>
    public static (string Signature, string? Docs)? ParseHover(string markdown)
    {
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

        var signature = CSharpSnippet.Unwrap(markdown[(first + 1)..close].Trim());

        if (signature.Length == 0)
        {
            return null;
        }

        var rest = markdown[(close + 4)..].Replace("&nbsp;", " ", StringComparison.Ordinal).ReplaceLineEndings("\n");
        var docs = string.Join('\n', rest.Split('\n').Select(x => x.TrimEnd())).Trim();
        return (signature, docs.Length == 0 ? null : CSharpSnippet.Unwrap(docs));
    }
}
