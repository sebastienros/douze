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
/// Runs the <c>twoslash</c> step with TypeScript 7 (the binary of <c>DOUZE_TEST_TYPESCRIPT</c>, or the pinned version
/// installed once in a temporary folder) and, where it is published, tsrs (<c>DOUZE_TEST_TSRS</c>). Skipped on a
/// platform without the engine, or offline.
/// </summary>
public sealed partial class TwoslashStepTests : CacheTestBase
{
    private static readonly Lazy<Task<(string? Path, string? Reason)>> TypeScriptBinary = new(() => ResolveAsync(NpmTool.TypeScript, "DOUZE_TEST_TYPESCRIPT"));
    private static readonly Lazy<Task<(string? Path, string? Reason)>> TsrsBinary = new(() => ResolveAsync(NpmTool.Tsrs, "DOUZE_TEST_TSRS"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(string?, string?)> ResolveAsync(NpmTool tool, string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path)
        {
            return (path, null);
        }

        if (!tool.Packages.ContainsKey(NpmTool.CurrentPlatform))
        {
            return (null, $"{tool.Name} has no binary for {NpmTool.CurrentPlatform}");
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

    private static async Task<string> RequireAsync(Lazy<Task<(string? Path, string? Reason)>> binary)
    {
        var (path, reason) = await binary.Value;

        if (path == null)
        {
            Assert.Skip(reason!);
        }

        return path;
    }

    private async Task<WorkflowDefinition> WorkflowAsync(params (string Key, Node? Value)[] options)
    {
        var engine = options.FirstOrDefault(x => x.Key == "engine").Value is NodeValue<string> { Value: "tsrs" } ? TsrsBinary : TypeScriptBinary;
        var with = StepTestBase.With([("path", NodeValue.Create(await RequireAsync(engine))), .. options]);

        return StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("markdown", "markdown", needs: ["pages"]),
            StepTestBase.Step("twoslash", "twoslash", needs: ["markdown"], with: with),
            StepTestBase.Step("write", "write", needs: ["twoslash"]));
    }

    private static string Fence(string code, string language = "ts", string meta = "twoslash") => $"```{language} {meta}\n{code}\n```\n\n";

    [GeneratedRegex("data-twoslash=\"([^\"]*)\"")]
    private static partial Regex TwoslashAttribute();

    /// <summary>
    /// The twoslash results of the fences of an output page, in order.
    /// </summary>
    private async Task<List<JsonElement>> ResultsAsync(string path)
    {
        var html = await Output.ReadAllTextAsync(path);
        return [.. TwoslashAttribute().Matches(html).Select(x => JsonDocument.Parse(WebUtility.HtmlDecode(x.Groups[1].Value)).RootElement.Clone())];
    }

    private static List<JsonElement> Nodes(JsonElement result, string type) =>
        [.. result.GetProperty("nodes").EnumerateArray().Where(x => x.GetProperty("type").GetString() == type)];

    private static string Text(JsonElement node) => node.GetProperty("text").GetString()!;

    private static (int Line, int Character) Position(JsonElement node) => (node.GetProperty("line").GetInt32(), node.GetProperty("character").GetInt32());

    [Fact]
    public async Task HoversEveryIdentifier()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            /** The answer. */
            const answer = 42;
            const doubled = answer * 2;
            console.log(doubled.toFixed(1));
            """) + Fence("const notTwoslash = 1;", meta: "title=\"x.ts\""));

        await BuildCachedAsync(await WorkflowAsync());

        var result = Assert.Single(await ResultsAsync("/docs/a.html"));
        var hovers = Nodes(result, "hover");

        Assert.Equal(["answer", "doubled", "answer", "console", "log", "doubled", "toFixed"], hovers.Select(x => x.GetProperty("target").GetString()));
        Assert.Equal("const answer: 42", Text(hovers[0]));
        Assert.Equal("The answer.", hovers[0].GetProperty("docs").GetString());
        Assert.Equal((1, 6), Position(hovers[0]));
        Assert.Equal("const doubled: number", Text(hovers[1]));
        Assert.StartsWith("(method) Number.toFixed(fractionDigits?: number): string", Text(hovers[6]));
        Assert.Equal(6, hovers[0].GetProperty("length").GetInt32());

        // The fence without twoslash in its meta has no result; the code of the result is the fence's.
        Assert.StartsWith("/** The answer. */\nconst answer = 42;", result.GetProperty("code").GetString());
        Assert.Contains(Messages, x => x.Message.Contains("checking 1 snippet(s) with", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallsCountTheOtherOverloads()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            function pick(a: string): string;
            function pick(a: number, b?: number): number;
            function pick(a: any) { return a; }
            pick("x");
            pick (1, 2);
            const parse = (text: string) => text.length;
            parse("x");
            [1].map<number>((x) => x);
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var hovers = Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "hover");
        var calls = hovers.Where(x => x.GetProperty("line").GetInt32() >= 3).GroupBy(x => (x.GetProperty("target").GetString(), x.GetProperty("line").GetInt32())).ToDictionary(x => x.Key, x => Text(x.First()));

        // As TypeScript 5 prints them at a call: the signature called, and the count of the others.
        Assert.Equal("function pick(a: string): string (+1 overload)", calls[("pick", 3)]);
        Assert.Equal("function pick(a: number, b?: number): number (+1 overload)", calls[("pick", 4)]);
        Assert.Equal("const parse: (text: string) => number", calls[("parse", 6)]);
        Assert.DoesNotContain("overload", calls[("map", 7)]);
    }

    [Fact]
    public async Task QueriesHighlightsAndTags()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @log: Logged
            const value = { name: "x", size: 1 };
            //    ^?
            const name = value.name;
            //    ^^^^ the name
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var result = Assert.Single(await ResultsAsync("/docs/a.html"));
        Assert.Equal("const value = { name: \"x\", size: 1 };\nconst name = value.name;\n", result.GetProperty("code").GetString());

        var query = Assert.Single(Nodes(result, "query"));
        Assert.Equal("const value: {\n    name: string;\n    size: number;\n}", Text(query));
        Assert.Equal((0, 6), Position(query));

        var highlight = Assert.Single(Nodes(result, "highlight"));
        Assert.Equal(((1, 6), 4, "the name"), (Position(highlight), highlight.GetProperty("length").GetInt32(), Text(highlight)));

        var tag = Assert.Single(Nodes(result, "tag"));
        Assert.Equal(("log", "Logged", (0, 0)), (tag.GetProperty("name").GetString(), Text(tag), Position(tag)));
    }

    [Fact]
    public async Task Completions()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            const text = "abc";
            text.toUpp
            //        ^|
            """));

        await BuildCachedAsync(await WorkflowAsync(("handbook", new NodeObject { ["noErrors"] = NodeValue.Create(true) })));

        var completion = Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "completion"));
        Assert.Equal("toUpp", completion.GetProperty("completionsPrefix").GetString());
        Assert.Contains(completion.GetProperty("completions").EnumerateArray(), x => x.GetProperty("name").GetString() == "toUpperCase");
        Assert.DoesNotContain(completion.GetProperty("completions").EnumerateArray(), x => x.GetProperty("name").GetString() == "toLowerCase");
    }

    [Fact]
    public async Task ExpectedErrors()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @errors: 2322
            let count: number = "one";
            """) + Fence("""
            // @noErrors
            let other: number = "two";
            """) + Fence("""
            // @noErrors: 2322
            let third: number = "three";
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var results = await ResultsAsync("/docs/a.html");
        var error = Assert.Single(Nodes(results[0], "error"));

        Assert.Equal(2322, error.GetProperty("code").GetInt32());
        Assert.Equal("Type 'string' is not assignable to type 'number'.", Text(error));
        Assert.Equal(("error", "index.ts", "err-2322-4-5"), (error.GetProperty("level").GetString(), error.GetProperty("filename").GetString(), error.GetProperty("id").GetString()));
        Assert.Equal((0, 4), Position(error));
        Assert.Empty(Nodes(results[1], "error"));
        Assert.Empty(Nodes(results[2], "error"));
    }

    [Fact]
    public async Task UnexpectedErrorsFailTheBuild()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("let count: number = \"one\";"));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        var error = Assert.Single(result.Errors);
        Assert.Contains("/docs/a.md: twoslash fence 1 ('let count: number = \"one\";'): errors were thrown in the sample, but not included in an error tag: 2322", error.Message);
        Assert.Contains("Expected: // @errors: 2322", error.Message);
    }

    [Fact]
    public async Task ErrorsCanBeWarnings()
    {
        await Source.WriteTextFileAsync("/docs/a.md", "# Title\n\n" + Fence("let count: number = \"one\";") + Fence("""
            const a = 1;
            // ---cut-end---
            """));

        await BuildCachedAsync(await WorkflowAsync(("errors", NodeValue.Create("warn"))));

        // The unexpected error is a warning and is rendered; the invalid snippet is a warning and has no result.
        var results = await ResultsAsync("/docs/a.html");
        Assert.Equal(2322, Assert.Single(Nodes(Assert.Single(results), "error")).GetProperty("code").GetInt32());
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("/docs/a.md: twoslash fence 1", StringComparison.Ordinal));
        Assert.Contains(Messages, x => x.Level == LogLevel.Warning && x.Message.Contains("twoslash fence 2 ('const a = 1;'): mismatched cut markers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnginesAgree()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @errors: 2322
            interface Point { x: number; y: number }
            /** Moves a point. */
            function move(p: Point, by: number): Point { return { x: p.x + by, y: p.y + by }; }
            const moved = move({ x: 1, y: 2 }, 3);
            //    ^?
            const wrong: string = moved.x;
            """));

        await BuildCachedAsync(await WorkflowAsync(("engine", NodeValue.Create("tsrs"))));
        var tsrs = (await ResultsAsync("/docs/a.html")).Single().GetRawText();

        await BuildCachedAsync(await WorkflowAsync());
        Assert.Equal(tsrs, (await ResultsAsync("/docs/a.html")).Single().GetRawText());
        Assert.Contains(Messages, x => x.Message.Contains("with Version 7.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoErrorValidationKeepsTheErrors()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("let count: number = \"one\";"));

        await BuildCachedAsync(await WorkflowAsync(("handbook", new NodeObject { ["noErrorValidation"] = NodeValue.Create(true) })));

        Assert.Single(Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "error"));
    }

    [Fact]
    public async Task HandbookOptions()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @noStaticSemanticInfo
            const a = 1;
            """) + Fence("""
            // @keepNotations
            const b = 1;
            //    ^?
            """) + Fence("""
            // @noErrorsCutted
            let hidden: number = "x";
            // ---cut---
            const c = 1;
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var results = await ResultsAsync("/docs/a.html");
        Assert.Empty(Nodes(results[0], "hover"));
        Assert.Equal("// @keepNotations\nconst b = 1;\n//    ^?", results[1].GetProperty("code").GetString());
        Assert.Equal((1, 6), Position(Assert.Single(Nodes(results[1], "query"))));
        Assert.Equal("const c = 1;", results[2].GetProperty("code").GetString());
        Assert.Empty(Nodes(results[2], "error"));
    }

    [Fact]
    public async Task Cuts()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            const hidden = 1;
            // ---cut---
            const shown = hidden;
            // ---cut-start---
            const middle = 2;
            // ---cut-end---
            const last = middle;
            // ---cut-after---
            const after = 3;
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var result = Assert.Single(await ResultsAsync("/docs/a.html"));
        Assert.Equal("const shown = hidden;\nconst last = middle;\n", result.GetProperty("code").GetString());

        // The hovers of the removed code are gone; the others moved with the code.
        var hovers = Nodes(result, "hover");
        Assert.Equal(["shown", "hidden", "last", "middle"], hovers.Select(x => x.GetProperty("target").GetString()));
        Assert.Equal("const hidden: 1", Text(hovers[1]));
        Assert.Equal((1, 6), Position(hovers[2]));
    }

    [Fact]
    public async Task MultipleFiles()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @filename: math.ts
            export function add(a: number, b: number) { return a + b; }
            // @filename: main.ts
            import { add } from "./math";
            const sum = add(1, 2);
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var result = Assert.Single(await ResultsAsync("/docs/a.html"));
        var hovers = Nodes(result, "hover");

        Assert.Contains(hovers, x => Text(x) == "function add(a: number, b: number): number");
        Assert.Contains(hovers, x => Text(x) == "const sum: number");
        Assert.Contains(hovers, x => Text(x).StartsWith("(alias) function add(a: number, b: number): number", StringComparison.Ordinal));

        // The file lines stay.
        Assert.StartsWith("// @filename: math.ts\n", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CompilerOptionNotationsGetTheirOwnProject()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @strict: false
            function loose(x) { return x; }
            """) + Fence("""
            // @errors: 7006
            function strict(x) { return x; }
            """));

        await BuildCachedAsync(await WorkflowAsync());

        var results = await ResultsAsync("/docs/a.html");
        Assert.Empty(Nodes(results[0], "error"));
        Assert.Equal(7006, Assert.Single(Nodes(results[1], "error")).GetProperty("code").GetInt32());
        Assert.Contains(Messages, x => x.Message.Contains("with 2 language server(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedFilesAndCompilerOptions()
    {
        await Source.WriteTextFileAsync("/types/sdk.d.ts", "/** Creates a client. */\nexport declare function createClient(name: string): { send(message: string): Promise<void> };\n");
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            import { createClient } from "./modules/sdk.js";
            const client = createClient("a");
            await client.send("hello");
            """));

        await BuildCachedAsync(await WorkflowAsync(
            ("files", new NodeObject { ["modules/sdk.d.ts"] = NodeValue.Create("types/sdk.d.ts") }),
            ("compiler", new NodeObject { ["moduleResolution"] = NodeValue.Create("bundler"), ["lib"] = new NodeArray { NodeValue.Create("es2022"), NodeValue.Create("dom") } })));

        var hovers = Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "hover");

        Assert.Equal("const client: {\n    send(message: string): Promise<void>;\n}", Text(hovers.Single(x => x.GetProperty("target").GetString() == "client" && Position(x) == (1, 6))));
        Assert.Equal("Creates a client.", hovers[0].GetProperty("docs").GetString());
    }

    [Fact]
    public async Task GlobalDeclarationsDoNotLeak()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            declare global { var injected: string; }
            export {};
            """) + Fence("""
            // @errors: 2304
            injected;
            """));

        await BuildCachedAsync(await WorkflowAsync());

        Assert.Equal(2304, Assert.Single(Nodes((await ResultsAsync("/docs/a.html"))[1], "error")).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Jsx()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @jsx: preserve
            declare global { namespace JSX { interface IntrinsicElements { div: { title?: string } } } }
            const label = "it's";
            const element = <div title={label}>don't {label}</div>;
            """, language: "tsx"));

        await BuildCachedAsync(await WorkflowAsync());

        var hovers = Nodes(Assert.Single(await ResultsAsync("/docs/a.html")), "hover");
        Assert.Contains(hovers, x => x.GetProperty("target").GetString() == "title" && Text(x) == "(property) title?: string | undefined");
        Assert.Equal(3, hovers.Count(x => x.GetProperty("target").GetString() == "label"));
    }

    [Fact]
    public async Task UnchangedSnippetsAreNotCheckedAgain()
    {
        var workflow = await WorkflowAsync();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("const a = 1;"));
        await Source.WriteTextFileAsync("/docs/b.md", Fence("const b = 2;"));

        await BuildCachedAsync(workflow);
        Assert.Contains(Messages, x => x.Message.Contains("checking 2 snippet(s)", StringComparison.Ordinal));

        // A page changes: its new snippet is checked, the other one is memoized.
        Messages.Clear();
        await Source.WriteTextFileAsync("/docs/a.md", Fence("const a = 1;") + Fence("const c = 3;"));
        await BuildCachedAsync(workflow);

        Assert.Contains(Messages, x => x.Message.Contains("checking 1 snippet(s)", StringComparison.Ordinal));
        Assert.Equal(2, (await ResultsAsync("/docs/a.html")).Count);

        // Nothing changes: the step's unit is replayed, and no tsrs runs.
        Messages.Clear();
        var result = await BuildCachedAsync(workflow);
        Assert.Empty(Executed(result, "twoslash"));
        Assert.DoesNotContain(Messages, x => x.Message.Contains("checking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidOptions()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("const a = 1;"));

        var e = await Assert.ThrowsAsync<WorkflowValidationException>(() => BuildAsync(StepTestBase.Workflow(
            StepTestBase.Step("pages", "frontmatter", inputs: ["docs/**/*.md"]),
            StepTestBase.Step("twoslash", "twoslash", needs: ["pages"], with: StepTestBase.With(("compiler", new NodeObject { ["strictt"] = NodeValue.Create(true) })))), Options()));

        Assert.Contains("unknown compiler option 'strictt'; did you mean 'strict'?", e.Message);
    }

    [Fact]
    public async Task InvalidCompilerOptionValues()
    {
        await Source.WriteTextFileAsync("/docs/a.md", Fence("""
            // @target: es1999
            const a = 1;
            """));

        var result = await BuildAsync(await WorkflowAsync(), Options());

        Assert.Contains("'es1999' is not a value of the compiler option 'target'", Assert.Single(result.Errors).Message);
    }
}
