using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Cache;
using Douze.Tools;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Steps;

/// <summary>
/// Runs the <c>csharp</c> step with the Roslyn language server (the <c>Microsoft.CodeAnalysis.LanguageServer.dll</c> of
/// <c>DOUZE_TEST_ROSLYN</c>, or the pinned version installed once in a temporary folder) and the .NET SDK of the
/// <c>dotnet</c> on the <c>PATH</c>. Skipped without the SDK, or offline.
/// </summary>
public sealed partial class CSharpStepTests : CacheTestBase
{
    private static readonly Lazy<Task<(string? Path, string? Reason)>> Server = new(ResolveAsync);

    private static async Task<(string?, string?)> ResolveAsync()
    {
        try
        {
            using var dotnet = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { "--version" }, RedirectStandardOutput = true, RedirectStandardError = true });
            var version = await dotnet!.StandardOutput.ReadToEndAsync();
            await dotnet.WaitForExitAsync();

            if (dotnet.ExitCode != 0 || version.Length == 0 || !char.IsAsciiDigit(version[0]))
            {
                return (null, "the .NET SDK is not installed");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, "dotnet is not on the PATH");
        }

        if (Environment.GetEnvironmentVariable("DOUZE_TEST_ROSLYN") is { Length: > 0 } path)
        {
            return (path, null);
        }

        var tool = NuGetTool.RoslynLanguageServer;

        if (!tool.Packages.ContainsKey(NpmTool.CurrentPlatform))
        {
            return (null, $"{tool.Name} has no package for {NpmTool.CurrentPlatform}");
        }

        try
        {
            var installer = new ToolInstaller(Path.Combine(Path.GetTempPath(), "douze-tests-tools"), null, (_, _) => { });
            return (await installer.InstallAsync(tool, tool.DefaultVersion, CancellationToken.None), null);
        }
        catch (DouzeException e)
        {
            return (null, $"cannot install {tool.Name}: {e.Message}");
        }
    }

    private static async Task<WorkflowDefinition> WorkflowAsync(params (string Key, Node? Value)[] options)
    {
        var (path, reason) = await Server.Value;

        if (path == null)
        {
            Assert.Skip(reason!);
        }

        return StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
            StepTestBase.Step("csharp", "csharp", needs: ["markdown"], with: StepTestBase.With([("path", NodeValue.Create(path)), .. options])),
            StepTestBase.Step("write", "write", needs: ["csharp"]));
    }

    private static string Fence(string code, string meta = "") => $"```csharp {meta}\n{code}\n```\n\n";

    [GeneratedRegex("data-twoslash=\"([^\"]*)\"")]
    private static partial Regex TwoslashAttribute();

    private async Task<List<JsonElement>> ResultsAsync(string path)
    {
        var html = await Output.ReadAllTextAsync(path);
        return [.. TwoslashAttribute().Matches(html).Select(x => JsonDocument.Parse(WebUtility.HtmlDecode(x.Groups[1].Value)).RootElement.Clone())];
    }

    private static List<JsonElement> Nodes(JsonElement result, string type) =>
        [.. result.GetProperty("nodes").EnumerateArray().Where(x => x.GetProperty("type").GetString() == type)];

    private static JsonElement Hover(JsonElement result, string target, int index = 0) =>
        Nodes(result, "hover").Where(x => x.GetProperty("target").GetString() == target).ElementAt(index);

    private static string Text(JsonElement node) => node.GetProperty("text").GetString()!;

    private static (int Line, int Character) Position(JsonElement node) => (node.GetProperty("line").GetInt32(), node.GetProperty("character").GetInt32());

    [Fact]
    public async Task HoversEverySymbol()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            using System.Text;

            var numbers = new List<int> { 1, 2, 3 };
            var text = new StringBuilder();
            text.Append(numbers.Count);
            Console.WriteLine(text);
            """) + Fence("""
            /// <summary>Greets people.</summary>
            public sealed class Greeter
            {
                public string Greet(string name) => $"Hello {name}";
            }
            """) + Fence("""
            var point = new Point(1, 2);
            Console.WriteLine(point.X);

            record Point(int X, int Y);
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var results = await ResultsAsync("/docs/a.html");
        Assert.Equal(3, results.Count);

        // Top-level statements: locals, types, methods and properties; not keywords.
        var statements = results[0];
        Assert.StartsWith("using System.Text;\n\nvar numbers", statements.GetProperty("code").GetString());
        Assert.Contains("List<int>", Text(Hover(statements, "numbers")));
        Assert.Equal((2, 4), Position(Hover(statements, "numbers")));
        Assert.Contains("StringBuilder.Append(int value)", Text(Hover(statements, "Append")));
        Assert.Contains("Console.WriteLine", Text(Hover(statements, "WriteLine")));
        Assert.Contains("List<int>.Count", Text(Hover(statements, "Count")));
        Assert.DoesNotContain(Nodes(statements, "hover"), x => x.GetProperty("target").GetString() is "var" or "new" or "using");
        Assert.Empty(Nodes(statements, "error"));

        // Declarations, with their documentation and without the wrapping's names.
        var greeter = Hover(results[1], "Greeter");
        Assert.Equal("class Greeter", Text(greeter));
        Assert.Equal("Greets people\\.", greeter.GetProperty("docs").GetString());
        Assert.Contains("string Greeter.Greet(string name)", Text(Hover(results[1], "Greet")));

        // Statements then a type declaration.
        Assert.Contains("record Point", Text(Hover(results[2], "Point", 1)));
        Assert.Empty(Nodes(results[2], "error"));
        Assert.DoesNotContain(results.SelectMany(x => Nodes(x, "hover")), x => Text(x).Contains("__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnippetsDoNotSeeEachOther()
    {
        // The same names in two snippets, and a snippet that uses a type of another one.
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var item = new Item();\nclass Item { }") + Fence("var item = new Item();\nclass Item { public int Size; }"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var item = new Item();"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        var error = Assert.Single(result.Errors);
        Assert.Contains("/docs/b.md: csharp fence 1 ('var item = new Item();'): 1 error(s): CS0246 on line 1", error.Message);
        Assert.DoesNotContain("/docs/a.md", error.Message);
    }

    [Fact]
    public async Task ErrorsFailTheBuild()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("int count = \"one\";"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        Assert.Contains("/docs/a.md: csharp fence 1 ('int count = \"one\";'): 1 error(s): CS0029 on line 1: Cannot implicitly convert type 'string' to 'int'", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task ErrorsCanBeWarnings()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("var count = 1;\nint other = \"one\";", "title=\"Program.cs\""));

        await BuildCachedAsync(await WorkflowAsync(("errors", NodeValue.Create("warn"))));

        // The error is a warning, and is rendered.
        var error = Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
        Assert.Equal(("CS0029", 29, "error", "Program.cs"), (error.GetProperty("code").GetString(), 29, error.GetProperty("level").GetString(), error.GetProperty("filename").GetString()));
        Assert.Equal((1, 12), Position(error));
        Assert.Equal(5, error.GetProperty("length").GetInt32());
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: csharp fence 1 ('var count = 1;'): 1 error(s): CS0029 on line 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemoizedErrorsAreReportedAgain()
    {
        var workflow = await WorkflowAsync(("errors", NodeValue.Create("warn")));
        await Source.WriteTextFileAsync("/docs/a.md", Fence("int count = \"one\";"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 2;"));

        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: csharp fence 1", StringComparison.Ordinal));

        // Another page changes: the snippet with errors is not checked again, but still reported and rendered.
        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 3;"));
        await BuildCachedAsync(workflow);

        Assert.Contains(Messages, x => x.Message.Contains("checking 1 C# snippet(s)", StringComparison.Ordinal));
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: csharp fence 1 ('int count = \"one\";'): 1 error(s): CS0029", StringComparison.Ordinal));
        Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
    }

    [Fact]
    public async Task ContextsAndPatterns()
    {
        // Fragments use a variable of an earlier snippet, which the context class declares; a snippet's own local hides it.
        await Source.WriteTextFileAsync("/stubs/Context.cs", "public class SnippetContext\n{\n    protected System.Text.StringBuilder text = new();\n}\n");
        await Source.WriteTextFileAsync("/docs/a.md", Fence("text.Append(1);") + Fence("var text = 1;\nConsole.WriteLine(text + 1);") + Fence("var other = 1;"));

        var projects = new NodeObject
        {
            ["fragments"] = new NodeObject
            {
                ["files"] = new NodeObject { ["Context.cs"] = NodeValue.Create("stubs/Context.cs") },
                ["context"] = NodeValue.Create("SnippetContext"),
                ["patterns"] = new NodeArray { NodeValue.Create(@"\btext\b") },
            },
        };

        await BuildCachedAsync(await WorkflowAsync(("projects", projects)));

        var results = await ResultsAsync("/docs/a.html");
        Assert.Equal("(field) System.Text.StringBuilder SnippetContext.text", Text(Hover(results[0], "text")));
        Assert.Equal("(local variable) int text", Text(Hover(results[1], "text")));
        Assert.Empty(results.SelectMany(x => Nodes(x, "error")));
        Assert.Contains(Messages, x => x.Message.Contains("checking 3 C# snippet(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IgnoredErrors()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("int count = \"one\";"));

        await BuildCachedAsync(await WorkflowAsync(("ignore", new NodeArray { NodeValue.Create("CS0029") })));

        Assert.Empty(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
    }

    [Fact]
    public async Task ProjectsWithPackagesAndSharedFiles()
    {
        await Source.WriteTextFileAsync("/stubs/Projects.cs", "namespace Projects { public sealed class Api { } }\n");
        await Source.WriteTextFileAsync("/docs/a.md",
            Fence("""Console.WriteLine("some_text".Humanize());""", "title=\"C# — Humanize.cs\"")
            + Fence("""var api = typeof(Projects.Api);""", "project=humanizer")
            + Fence("""Console.WriteLine("some_text".Humanize());"""));

        var projects = new NodeObject
        {
            ["humanizer"] = new NodeObject
            {
                ["packages"] = new NodeObject { ["Humanizer.Core"] = NodeValue.Create("2.14.1") },
                ["usings"] = new NodeArray { NodeValue.Create("Humanizer") },
                ["files"] = new NodeObject { ["Projects.cs"] = NodeValue.Create("stubs/Projects.cs") },
                ["titles"] = new NodeArray { NodeValue.Create("Humanize.cs") },
            },
        };

        await BuildCachedAsync(await WorkflowAsync(("projects", projects), ("errors", NodeValue.Create("warn"))));

        var results = await ResultsAsync("/docs/a.html");
        Assert.Contains("(extension) string string.Humanize()", Text(Hover(results[0], "Humanize")));
        Assert.Empty(Nodes(results[0], "error"));
        Assert.Contains("class Projects.Api", Text(Hover(results[1], "Api")));
        Assert.Empty(Nodes(results[1], "error"));

        // The default project has no package.
        Assert.Equal("CS1061", Assert.Single(Nodes(results[2], "error")).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnchangedSnippetsAreNotCheckedAgain()
    {
        var workflow = await WorkflowAsync();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1;"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 2;"));

        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Message.Contains("checking 2 C# snippet(s)", StringComparison.Ordinal));

        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1;") + Fence("var c = 3;"));
        await BuildCachedAsync(workflow);

        Assert.Contains(Messages, x => x.Message.Contains("checking 1 C# snippet(s)", StringComparison.Ordinal));
        Assert.Equal(2, (await ResultsAsync("/docs/a.html")).Count);
    }

    [Fact]
    public async Task InvalidProjects()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1;"));

        var e = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync(StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("csharp", "csharp", needs: ["pages"], with: StepTestBase.With(("projects", new NodeObject { ["app"] = new NodeObject { ["package"] = new NodeObject() } })))), Options()));

        Assert.Contains("project 'app' of step 'csharp' has an unknown field 'package'; did you mean 'packages'?", e.Message);
    }

    [Fact]
    public async Task UnknownProjectsAreErrors()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1;", "project=missing"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        Assert.Contains("csharp fence 1 ('var a = 1;'): unknown project 'missing' (the projects are default)", Assert.Single(result.Errors).Message);
    }
}
