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
/// Runs the <c>go</c> step with gopls (the executable of <c>DOUZE_TEST_GOPLS</c>, or the pinned version installed once
/// in a temporary folder with <c>go install</c>) and the <c>go</c> on the <c>PATH</c>. Skipped without Go, or offline.
/// </summary>
public sealed partial class GoStepTests : CacheTestBase
{
    private static readonly Lazy<Task<(string? Path, string? Reason)>> Server = new(ResolveAsync);

    private static async Task<(string?, string?)> ResolveAsync()
    {
        try
        {
            using var go = Process.Start(new ProcessStartInfo("go") { ArgumentList = { "env", "GOVERSION" }, RedirectStandardOutput = true, RedirectStandardError = true });
            var version = await go!.StandardOutput.ReadToEndAsync();
            await go.WaitForExitAsync();

            if (go.ExitCode != 0 || !version.StartsWith("go", StringComparison.Ordinal))
            {
                return (null, "Go is not installed");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, "go is not on the PATH");
        }

        if (Environment.GetEnvironmentVariable("DOUZE_TEST_GOPLS") is { Length: > 0 } path)
        {
            return (path, null);
        }

        var tool = GoTool.Gopls;

        try
        {
            var installer = new ToolInstaller(Path.Combine(Path.GetTempPath(), "douze-tests-tools"), null, (_, _) => { });
            return (await installer.InstallAsync(tool, tool.DefaultVersion, "go", CancellationToken.None), null);
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

        return Workflow(path, options);
    }

    private static WorkflowDefinition Workflow(string path, params (string Key, Node? Value)[] options) => StepTestBase.Workflow(
        StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
        StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
        StepTestBase.Step("go", "go", needs: ["markdown"], with: StepTestBase.With([("path", NodeValue.Create(path)), .. options])),
        StepTestBase.Step("write", "write", needs: ["go"]));

    private static string Fence(string code, string meta = "") => $"```go {meta}\n{code}\n```\n\n";

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

    private static string? Docs(JsonElement node) => node.TryGetProperty("docs", out var docs) ? docs.GetString() : null;

    private static (int Line, int Character) Position(JsonElement node) => (node.GetProperty("line").GetInt32(), node.GetProperty("character").GetInt32());

    [Fact]
    public async Task HoversEverySymbol()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            package main

            import (
            	"fmt"
            	"strings"
            )

            // Greeter greets people.
            type Greeter struct{ Name string }

            // Greet returns a greeting.
            func (g Greeter) Greet() string { return "Hello " + g.Name }

            func main() {
            	g := Greeter{Name: "Go"}
            	fmt.Println(strings.ToUpper(g.Greet()))
            }
            """) + Fence("""
            // Add adds two numbers.
            func Add(a, b int) int { return a + b }
            """) + Fence("""
            values := []int{1, 2, 3}
            for _, value := range values {
            	println(value)
            }
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var results = await ResultsAsync("/docs/a.html");
        Assert.Equal(3, results.Count);

        // A file: packages, types, fields, methods, functions and variables; not keywords.
        var file = results[0];
        Assert.StartsWith("package main\n\nimport (", file.GetProperty("code").GetString());
        Assert.Equal("type Greeter struct{ Name string }", Text(Hover(file, "Greeter")));
        Assert.StartsWith("Greeter greets people.", Docs(Hover(file, "Greeter")));
        Assert.Equal((8, 5), Position(Hover(file, "Greeter")));
        Assert.Equal("func (g Greeter) Greet() string", Text(Hover(file, "Greet")));
        Assert.Equal("Greet returns a greeting.", Docs(Hover(file, "Greet")));
        Assert.Equal("field Name string", Text(Hover(file, "Name")));
        Assert.Equal("var g Greeter", Text(Hover(file, "g", 1)));
        Assert.Equal("func fmt.Println(a ...any) (n int, err error)", Text(Hover(file, "Println")));
        Assert.Equal("func strings.ToUpper(s string) string", Text(Hover(file, "ToUpper")));

        // A package's documentation is its first paragraph, without links to the Go sources.
        Assert.Equal("package fmt", Text(Hover(file, "fmt")));
        Assert.DoesNotContain("\n\n", Docs(Hover(file, "fmt")));
        Assert.DoesNotContain(Nodes(file, "hover"), x => x.GetProperty("target").GetString() is "func" or "type" or "struct" or "return" or "import");
        Assert.Empty(Nodes(file, "error"));

        // Declarations without a package clause: positions are the snippet's.
        Assert.Equal("func Add(a int, b int) int", Text(Hover(results[1], "Add")));
        Assert.Equal((1, 5), Position(Hover(results[1], "Add")));
        Assert.Empty(Nodes(results[1], "error"));

        // Statements, wrapped in a function.
        Assert.Equal("var values []int", Text(Hover(results[2], "values")));
        Assert.Equal((0, 0), Position(Hover(results[2], "values")));
        Assert.Equal("var value int", Text(Hover(results[2], "value")));
        Assert.Empty(Nodes(results[2], "error"));

        var hovers = results.SelectMany(x => Nodes(x, "hover")).ToList();
        Assert.DoesNotContain(hovers, x => Text(x).Contains("size=", StringComparison.Ordinal) || (Docs(x) ?? "").Contains("file://", StringComparison.Ordinal));
        Assert.DoesNotContain(hovers, x => x.GetProperty("target").GetString() == "_");
    }

    [Fact]
    public async Task SnippetsDoNotSeeEachOther()
    {
        // The same names in two snippets, and a snippet that uses a type of another one.
        await Source.WriteTextFileAsync("/docs/a.md", Fence("type Item struct{}\n\nfunc main() { _ = Item{} }") + Fence("type Item struct{ Size int }\n\nfunc main() { _ = Item{} }"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("_ = Item{}"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        var error = Assert.Single(result.Errors);
        Assert.Contains("/docs/b.md: go fence 1 ('_ = Item{}'): 1 error(s): UndeclaredName on line 1: undefined: Item", error.Message);
        Assert.DoesNotContain("/docs/a.md", error.Message);
    }

    [Fact]
    public async Task ErrorsFailTheBuild()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("var count int = \"one\""));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        Assert.Contains("/docs/a.md: go fence 1 ('var count int = \"one\"'): 1 error(s): IncompatibleAssign on line 1: cannot use \"one\" (untyped string constant) as int value in variable declaration", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task ErrorsCanBeWarnings()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("var total = 1\nvar count int = \"one\"", "title=\"cmd/server.go\""));

        await BuildCachedAsync(await WorkflowAsync(("errors", NodeValue.Create("warn"))));

        // The error is a warning, and is rendered.
        var error = Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
        Assert.Equal(("IncompatibleAssign", "error", "server.go"), (error.GetProperty("code").GetString(), error.GetProperty("level").GetString(), error.GetProperty("filename").GetString()));
        Assert.Equal((1, 16), Position(error));
        Assert.Equal(5, error.GetProperty("length").GetInt32());
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: go fence 1 ('var total = 1'): 1 error(s): IncompatibleAssign on line 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemoizedErrorsAreReportedAgain()
    {
        var workflow = await WorkflowAsync(("errors", NodeValue.Create("warn")));
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var count int = \"one\""));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 2"));

        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: go fence 1", StringComparison.Ordinal));

        // Another page changes: the snippet with errors is not checked again, but still reported and rendered.
        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 3"));
        await BuildCachedAsync(workflow);

        Assert.Contains(Messages, x => x.Message.Contains("checking 1 Go snippet(s)", StringComparison.Ordinal));
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: go fence 1 ('var count int = \"one\"'): 1 error(s): IncompatibleAssign", StringComparison.Ordinal));
        Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
    }

    [Fact]
    public async Task IgnoredErrors()
    {
        // A fragment's variables are often used by the next one.
        await Source.WriteTextFileAsync("/docs/a.md", Fence("count := 1"));

        await BuildCachedAsync(await WorkflowAsync(("ignore", new NodeArray { NodeValue.Create("UnusedVar") })));

        Assert.Empty(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
    }

    [Fact]
    public async Task ModulesWithRequiresAndSharedFiles()
    {
        await Source.WriteTextFileAsync("/stubs/api.go", "// Package api is a stub.\npackage api\n\n// Client calls the API.\ntype Client struct{}\n");
        await Source.WriteTextFileAsync("/docs/a.md",
            Fence("import \"github.com/google/uuid\"\n\nvar id = uuid.New()", "title=\"ids/main.go\"")
            + Fence("import \"snippets/api\"\n\nvar client api.Client", "module=ids")
            + Fence("import \"github.com/google/uuid\"\n\nvar id = uuid.New()"));

        var modules = new NodeObject
        {
            ["ids"] = new NodeObject
            {
                ["requires"] = new NodeObject { ["github.com/google/uuid"] = NodeValue.Create("v1.6.0") },
                ["files"] = new NodeObject { ["api/api.go"] = NodeValue.Create("stubs/api.go") },
                ["titles"] = new NodeArray { NodeValue.Create("ids/main.go") },
            },
        };

        await BuildCachedAsync(await WorkflowAsync(("modules", modules), ("errors", NodeValue.Create("warn"))));

        var results = await ResultsAsync("/docs/a.html");
        Assert.Equal("func uuid.New() uuid.UUID", Text(Hover(results[0], "New")));
        Assert.Empty(Nodes(results[0], "error"));
        Assert.Equal("type Client struct{}", Text(Hover(results[1], "Client")));
        Assert.Equal("Client calls the API.", Docs(Hover(results[1], "Client")));
        Assert.Empty(Nodes(results[1], "error"));

        // The default module requires nothing.
        Assert.Equal("BrokenImport", Assert.Single(Nodes(results[2], "error")).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnchangedSnippetsAreNotCheckedAgain()
    {
        var workflow = await WorkflowAsync();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("var b = 2"));

        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Message.Contains("checking 2 Go snippet(s)", StringComparison.Ordinal));

        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1") + Fence("var c = 3"));
        await BuildCachedAsync(workflow);

        Assert.Contains(Messages, x => x.Message.Contains("checking 1 Go snippet(s)", StringComparison.Ordinal));
        Assert.Equal(2, (await ResultsAsync("/docs/a.html")).Count);
    }

    [Fact]
    public async Task InvalidModules()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1"));

        var e = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync(StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("go", "go", needs: ["pages"], with: StepTestBase.With(("modules", new NodeObject { ["app"] = new NodeObject { ["require"] = new NodeObject() } })))), Options()));

        Assert.Contains("module 'app' of step 'go' has an unknown field 'require'; did you mean 'requires'?", e.Message);

        e = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync(StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("go", "go", needs: ["pages"], with: StepTestBase.With(("modules", new NodeObject { ["app"] = new NodeObject { ["files"] = new NodeObject { ["s1/a.go"] = NodeValue.Create("a.go") } } })))), Options()));

        Assert.Contains("has a reserved path 's1/a.go'", e.Message);
    }

    [Fact]
    public async Task UnknownModulesAreErrors()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1", "module=missing"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        Assert.Contains("go fence 1 ('var a = 1'): unknown module 'missing' (the modules are default)", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task RequiresGo()
    {
        // Any existing file stands for gopls, which does not start.
        await Source.WriteTextFileAsync("/docs/a.md", Fence("var a = 1"));

        var result = await BuildAsync(Workflow(typeof(GoStepTests).Assembly.Location, ("go", NodeValue.Create("/missing/go"))), Options());

        Assert.Contains("step 'go' requires Go: ", Assert.Single(result.Errors).Message);
    }
}
