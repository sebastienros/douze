using System.Diagnostics;
using System.Text.Json.Nodes;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Commands;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class FilterCommandStepTests : CommandTestBase
{
    [Fact]
    public async Task ContentTransformed()
    {
        await Source.WriteTextFileAsync("/docs/a.txt", "hello");
        await Source.WriteTextFileAsync("/docs/b.txt", "world");
        await Source.WriteTextFileAsync("/docs/c.md", "untouched");

        var result = await RunSuccessfullyAsync(Workflow(
            Command("upper", Tool("filter-upper"), CommandMode.Filter, inputs: ["**"], match: ["**/*.txt"])));

        var run = result["upper"];
        Assert.Equal(2, run.FilesConsumed);
        Assert.Equal(1, run.FilesPassedThrough);
        Assert.Equal("HELLO", await OutputTextAsync(run, "/docs/a.txt"));
        Assert.Equal("WORLD", await OutputTextAsync(run, "/docs/b.txt"));

        var a = Outputs(run).Single(x => x.Path == "/docs/a.txt");
        Assert.True(a.Content.IsText);
        Assert.Equal("upper", a.Provenance.Step);
    }

    [Fact]
    public async Task CommandSpansCarryTheirStep()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");
        var trace = new TraceRecorder();

        var result = await RunAsync(Workflow(Command("upper", Tool("filter-upper"), CommandMode.Filter, inputs: ["**"])), trace: trace);

        Assert.True(result.Succeeded);
        var span = Assert.Single(trace.Session.Events, x => x.Category == "command");
        Assert.Contains(new KeyValuePair<string, string>("step", "upper"), span.Args);
    }

    [Fact]
    public async Task Environment()
    {
        await Source.WriteTextFileAsync("/docs/a.html", "---\ntitle: A\n---\nbody");

        var result = await RunSuccessfullyAsync(Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Command("env", Tool("env", "DOUZE_PATH", "DOUZE_CONTENT_TYPE", "DOUZE_METADATA", "DOUZE_STEP", "CUSTOM"), CommandMode.Filter,
                needs: ["pages"], env: [("CUSTOM", "{{ file.name }} in {{ step }}")])));

        var lines = (await OutputTextAsync(result["env"], "/docs/a.html")).Split('\n');
        Assert.Equal("DOUZE_PATH=/docs/a.html", lines[0]);
        Assert.Equal("DOUZE_CONTENT_TYPE=text/html", lines[1]);
        Assert.Equal("A", JsonNode.Parse(lines[2]["DOUZE_METADATA=".Length..])!["title"]!.GetValue<string>());
        Assert.Equal("DOUZE_STEP=env", lines[3]);
        Assert.Equal("CUSTOM=a.html in env", lines[4]);
    }

    [Fact]
    public async Task LargeMetadataGoesThroughAFile()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");

        var big = new string('x', FilterCommandStep.MaxMetadataLength);
        var definition = Command("env", Tool("env", "DOUZE_METADATA", "DOUZE_METADATA_FILE"), CommandMode.Filter, needs: ["meta"]);

        var result = await RunSuccessfullyAsync(Workflow(
            Command("meta", Tool("filter-upper"), CommandMode.Filter, inputs: ["**"]),
            definition),
            verbose: false);

        // The metadata of a.txt is small: inline.
        Assert.StartsWith("DOUZE_METADATA={}", await OutputTextAsync(result["env"], "/a.txt"));

        Source = new();
        Site = null;
        await Source.WriteTextFileAsync("/a.html", $"---\nbig: {big}\n---\n");

        result = await RunSuccessfullyAsync(Workflow(Step("meta", "frontmatter", inputs: ["**"]), definition));

        var lines = (await OutputTextAsync(result["env"], "/a.html")).Split('\n');
        Assert.Equal("DOUZE_METADATA=", lines[0]);
        Assert.StartsWith("DOUZE_METADATA_FILE=", lines[1]);

        // Deleted after the run.
        Assert.False(File.Exists(lines[1]["DOUZE_METADATA_FILE=".Length..]));
    }

    [Fact]
    public async Task NonZeroExitMessageIncludesFile()
    {
        await Source.WriteTextFileAsync("/docs/a.txt", "a");

        var result = await RunAsync(Workflow(Command("fail", Tool("fail", "3"), CommandMode.Filter, inputs: ["**"])));

        var error = Assert.Single(result.Errors);
        Assert.Equal("fail", error.Step);
        Assert.Equal("/docs/a.txt", error.File?.FullName);
        Assert.Equal($"command exited with code 3 for '/docs/a.txt': first line{System.Environment.NewLine}it failed", error.Message);
        Assert.Null(error.Exception);
    }

    [Theory]
    [InlineData(nameof(StderrPolicy.Fail))]
    [InlineData(nameof(StderrPolicy.Warn))]
    [InlineData(nameof(StderrPolicy.Ignore))]
    public async Task StderrPolicies(string name)
    {
        var policy = Enum.Parse<StderrPolicy>(name);
        await Source.WriteTextFileAsync("/a.txt", "a");

        var result = await RunAsync(Workflow(Command("cmd", Tool("stderr"), CommandMode.Filter, inputs: ["**"], stderr: policy)));

        const string Message = "command wrote to stderr for '/a.txt': warning: something to say";
        var warnings = Messages.Where(x => x.Level == LogLevel.Warning && x.Step != null).ToArray();

        switch (policy)
        {
            case StderrPolicy.Fail:
                Assert.Equal(Message, Assert.Single(result.Errors).Message);
                Assert.Empty(warnings);
                break;

            case StderrPolicy.Warn:
                Assert.True(result.Succeeded);
                Assert.Equal(("cmd", Message), (Assert.Single(warnings).Step, warnings[0].Message));
                break;

            case StderrPolicy.Ignore:
                Assert.True(result.Succeeded);
                Assert.Empty(warnings);
                break;
        }
    }

    [Fact]
    public async Task TimeoutKillsTheProcessTree()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");
        var pids = Path.Combine(TempFolder, "pids");

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(Workflow(
            Command("hang", Tool("hang", $"\"{pids}\""), CommandMode.Filter, inputs: ["**"], timeout: TimeSpan.FromSeconds(4))));

        Assert.Equal("command timed out after 4 s for '/a.txt'", Assert.Single(result.Errors).Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), stopwatch.Elapsed.ToString());

        // The tool and the child it started are both gone.
        foreach (var pid in File.ReadAllText(pids).Split(' ').Select(int.Parse))
        {
            await AssertExitedAsync(pid);
        }
    }

    [Fact]
    public async Task ParallelismIsBounded()
    {
        for (var i = 0; i < 6; i++)
        {
            await Source.WriteTextFileAsync($"/{i}.txt", "x");
        }

        var counts = Path.Combine(TempFolder, "counts");
        Directory.CreateDirectory(counts);

        await RunSuccessfullyAsync(Workflow(Command("count", Tool("filter-count", $"\"{counts}\""), CommandMode.Filter, inputs: ["**"])), parallelism: 2);

        var observed = Directory.GetFiles(counts, "*.count").Select(x => int.Parse(File.ReadAllText(x))).ToArray();
        Assert.Equal(6, observed.Length);
        Assert.All(observed, x => Assert.InRange(x, 1, 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathsReachTheCommandIntact(bool shell)
    {
        // A space and a single quote: both need quoting, for every shell.
        await Source.WriteTextFileAsync("/my docs/it's.txt", "x");
        await Source.WriteTextFileAsync("/plain.txt", "x");

        var result = await RunSuccessfullyAsync(Workflow(
            Command("args", Tool("args", "{{ file.path }}", "{{ file.name }}"), CommandMode.Filter, inputs: ["**"], shell: shell)));

        Assert.Equal("/my docs/it's.txt\nit's.txt", await OutputTextAsync(result["args"], "/my docs/it's.txt"));
        Assert.Equal("/plain.txt\nplain.txt", await OutputTextAsync(result["args"], "/plain.txt"));
    }

    [Fact]
    public async Task WorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(TempFolder, "site", "tools"));
        await Source.WriteTextFileAsync("/a.txt", "x");

        Site = await SiteOptions.DetectAsync(Source, Output, Path.Combine(TempFolder, "site"), null);

        var result = await RunSuccessfullyAsync(Workflow(
            Command("cwd", Tool("args", "{{ cwd }}"), CommandMode.Filter, inputs: ["**"], cwd: "tools")));

        Assert.Equal(Path.Combine(TempFolder, "site", "tools"), await OutputTextAsync(result["cwd"], "/a.txt"));

        var missing = await RunAsync(Workflow(Command("cwd", Tool("args"), CommandMode.Filter, inputs: ["**"], cwd: "missing")));
        Assert.Equal($"the working directory '{Path.Combine(TempFolder, "site", "missing")}' does not exist", Assert.Single(missing.Errors).Message);
    }

    [Fact]
    public void WithIsOnlyValidInWorkerMode()
    {
        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowGraph.Build(Workflow(
            Command("cmd", "tool", CommandMode.Filter, inputs: ["**"], with: With(("a", NodeValue.Create(1)))))));

        Assert.Equal("'with' is only valid with 'uses' or with 'mode: worker' (step 'cmd')", error.Errors.Single().Message);
    }

    internal static async Task AssertExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"process {pid} is still running");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
