using System.Diagnostics;
using System.Text.Json.Nodes;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

public class WorkerCommandStepTests : CommandTestBase
{
    /// <summary>
    /// The fake worker over the files of the source, after the front matter step (whose 'delay' slows a request down).
    /// </summary>
    internal static WorkflowDefinition Worker(string mode = "worker-echo", int concurrency = 1, TimeSpan? timeout = null, StderrPolicy stderr = StderrPolicy.Fail, NodeObject? with = null)
    {
        return Workflow(
            Step("pages", "frontmatter", inputs: ["**"]),
            Command("work", Tool(mode, concurrency.ToString()), CommandMode.Worker, needs: ["pages"], timeout: timeout, stderr: stderr, with: with));
    }

    private static decimal Stat(VirtualFile file, string name) => file.Metadata.Own[name]!.GetValue<decimal>();

    [Fact]
    public async Task Handshake()
    {
        await Source.WriteTextFileAsync("/a.txt", "HELLO");

        var result = await RunSuccessfullyAsync(Worker(concurrency: 3, with: With(("suffix", NodeValue.Create("!")))), parallelism: 4);

        var hello = JsonNode.Parse(await OutputTextAsync(result["work"], "/a.txt"))!;
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal(1, (int?)hello["protocol"]);
        Assert.Equal("work", (string?)hello["step"]);
        Assert.Equal(4, (int?)hello["concurrency"]);
        Assert.Equal("!", (string?)hello["options"]!["suffix"]);

        // min(ready.concurrency, hello.concurrency)
        Assert.Contains(Messages, x => x is { Level: LogLevel.Debug, Step: "work", Message: "worker 'fake' ready, 3 request(s) at once" });
    }

    [Theory]
    [InlineData(2, 4, 2)]
    [InlineData(16, 2, 2)]
    public async Task ConcurrencyHonored(int announced, int parallelism, int expected)
    {
        for (var i = 0; i < 6; i++)
        {
            await Source.WriteTextFileAsync($"/{i}.md", "---\ndelay: 200\n---\nx");
        }

        var result = await RunSuccessfullyAsync(Worker(concurrency: announced), parallelism: parallelism);

        var outputs = Outputs(result["work"]);
        Assert.Equal(6, outputs.Length);
        Assert.InRange(outputs.Max(x => Stat(x, "maxInflight")), 1, expected);
    }

    [Fact]
    public async Task OutOfOrderResponses()
    {
        await Source.WriteTextFileAsync("/a.md", "---\ndelay: 1000\n---\na");
        await Source.WriteTextFileAsync("/b.md", "b");

        var result = await RunSuccessfullyAsync(Worker(concurrency: 2));

        var run = result["work"];
        var a = Outputs(run).Single(x => x.Path == "/a.md");
        var b = Outputs(run).Single(x => x.Path == "/b.md");

        // b was answered first, and each answer reached its own request.
        Assert.Equal("A", await TextAsync(a));
        Assert.Equal("B", await TextAsync(b));
        Assert.True(Stat(b, "sequence") < Stat(a, "sequence"));
    }

    [Fact]
    public async Task ErrorFailsItsUnitOnly()
    {
        await Source.WriteTextFileAsync("/a.txt", "ERROR");
        await Source.WriteTextFileAsync("/b.txt", "b");

        var result = await RunAsync(Worker(), failure: FailurePolicy.KeepGoing);

        var error = Assert.Single(result.Errors);
        Assert.Equal("/a.txt", error.File?.FullName);
        Assert.Equal("TS2304: Cannot find name 'foo' (line 12, column 3)", error.Message);
        Assert.Equal("B", await OutputTextAsync(result["work"], "/b.txt"));
    }

    [Fact]
    public async Task LogMessagesForwarded()
    {
        await Source.WriteTextFileAsync("/a.txt", "LOG");

        var result = await RunSuccessfullyAsync(Worker());

        Assert.Equal("LOGGED", await OutputTextAsync(result["work"], "/a.txt"));
        Assert.Contains(Messages, x => x is { Level: LogLevel.Info, Step: "work", Message: "log from /a.txt" });
    }

    [Fact]
    public async Task EarlyExitFailsInFlightUnits()
    {
        await Source.WriteTextFileAsync("/a.md", "---\ndelay: 2000\n---\na");
        await Source.WriteTextFileAsync("/b.md", "EXIT");

        var result = await RunAsync(Worker(concurrency: 2), failure: FailurePolicy.KeepGoing);

        // a was in flight (or sent after the exit): both fail with the exit code and stderr.
        Assert.Equal(["/a.md", "/b.md"], result.Errors.Select(x => x.File?.FullName).Order());
        Assert.All(result.Errors, x => Assert.Equal("worker exited with code 3: crashing", x.Message));
    }

    [Fact]
    public async Task StdinClosedAfterShutdown()
    {
        // The fake worker exits only when stdin ends: the build completes without waiting for the grace delay.
        await Source.WriteTextFileAsync("/a.txt", "a");

        var stopwatch = Stopwatch.StartNew();
        await RunSuccessfullyAsync(Worker());

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());
        Assert.DoesNotContain(Messages, x => x.Level == LogLevel.Warning && x.Step == "work");
    }

    [Fact]
    public async Task NotJsonFailsTheWorker()
    {
        await Source.WriteTextFileAsync("/a.txt", "GARBAGE");

        var result = await RunAsync(Worker());

        Assert.Equal("worker wrote a line that is not JSON: this is not json", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task StderrPolicies()
    {
        await Source.WriteTextFileAsync("/a.txt", "STDERR");

        var failed = await RunAsync(Worker());
        Assert.Equal("worker wrote to stderr: worker warning", Assert.Single(failed.Errors).Message);

        var warned = await RunSuccessfullyAsync(Worker(stderr: StderrPolicy.Warn));
        Assert.Equal("STDERR", await OutputTextAsync(warned["work"], "/a.txt"));
        Assert.Contains(Messages, x => x is { Level: LogLevel.Warning, Step: "work", Message: "worker wrote to stderr: worker warning" });

        await RunSuccessfullyAsync(Worker(stderr: StderrPolicy.Ignore));
        Assert.DoesNotContain(Messages, x => x.Level == LogLevel.Warning && x.Step == "work");
    }

    [Fact]
    public async Task ResultShapes()
    {
        await Source.WriteTextFileAsync("/many.txt", "MANY");
        await Source.WriteTextFileAsync("/drop.txt", "DROP");
        await Source.WriteTextFileAsync("/rename.md", "RENAME");
        await Source.WriteTextFileAsync("/meta.md", "---\ntitle: original\nkept: yes\n---\nMETA");
        await Source.WriteBinaryFileAsync("/image.png", [0, 1, 254]);

        var result = await RunSuccessfullyAsync(Worker());
        var outputs = Outputs(result["work"]);

        Assert.Equal(["/image.png", "/many.txt", "/many.txt.copy", "/meta.md", "/rename.html"], outputs.Select(x => x.Path.FullName));
        Assert.Equal("one", await OutputTextAsync(result["work"], "/many.txt"));
        Assert.Equal("two", await OutputTextAsync(result["work"], "/many.txt.copy"));

        // A new path gets the content type of its extension.
        var renamed = outputs.Single(x => x.Path == "/rename.html");
        Assert.Equal("text/html", renamed.EffectiveContentType);
        Assert.Equal("<p>renamed</p>", await TextAsync(renamed));

        // Metadata merged over the input's, content kept.
        var meta = outputs.Single(x => x.Path == "/meta.md");
        Assert.Equal("META", await TextAsync(meta));
        Assert.Equal("replaced", meta.Metadata.Own["title"]!.GetValue<string>());
        Assert.Equal("yes", meta.Metadata.Own["kept"]!.GetValue<string>());
        Assert.Equal(1m, meta.Metadata.Own["extra"]!.GetValue<decimal>());

        // Binary content goes both ways in base64.
        var image = outputs.Single(x => x.Path == "/image.png");
        Assert.False(image.Content.IsText);
        Assert.Equal(new byte[] { 255, 254, 1 }, (await image.Content.ReadBytesAsync(TestContext.Current.CancellationToken)).ToArray());
    }

    [Fact]
    public async Task TimeoutKillsTheWorker()
    {
        await Source.WriteTextFileAsync("/a.txt", "HANG");

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(Worker(timeout: TimeSpan.FromSeconds(1)));

        Assert.Equal("worker timed out after 1 s for '/a.txt'", Assert.Single(result.Errors).Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task WorkerThatDoesNotStart()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");

        var result = await RunAsync(Workflow(Command("work", Tool("fail", "1"), CommandMode.Worker, inputs: ["**"])));

        Assert.Equal($"worker exited with code 1: first line{Environment.NewLine}it failed", Assert.Single(result.Errors).Message);

        // Without files, the failure is reported all the same.
        Source = new();
        Site = null;
        result = await RunAsync(Workflow(Command("work", Tool("fail", "1"), CommandMode.Worker, inputs: ["**"])));

        var error = Assert.Single(result.Errors);
        Assert.Null(error.File);
        Assert.Equal($"worker exited with code 1: first line{Environment.NewLine}it failed", error.Message);
    }
}

/// <summary>
/// Tests that change the worker delays, which are static: they run alone.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public class WorkerDelaysCollection;

[Collection(typeof(WorkerDelaysCollection))]
public class WorkerDelayTests : CommandTestBase
{
    [Fact]
    public async Task ShutdownWithinGrace()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");

        var grace = Douze.Workflow.Commands.WorkerProcess.ShutdownGrace;
        Douze.Workflow.Commands.WorkerProcess.ShutdownGrace = TimeSpan.FromSeconds(1);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await RunSuccessfullyAsync(WorkerCommandStepTests.Worker("worker-slow"));

            Assert.Equal("A", await OutputTextAsync(result["work"], "/a.txt"));
            Assert.Contains(Messages, x => x is { Level: LogLevel.Warning, Step: "work", Message: "worker did not exit within 1 s after 'shutdown' and was killed" });
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), stopwatch.Elapsed.ToString());
        }
        finally
        {
            Douze.Workflow.Commands.WorkerProcess.ShutdownGrace = grace;
        }
    }

    [Fact]
    public async Task HandshakeTimeout()
    {
        await Source.WriteTextFileAsync("/a.txt", "a");

        var timeout = Douze.Workflow.Commands.WorkerProcess.HandshakeTimeout;
        Douze.Workflow.Commands.WorkerProcess.HandshakeTimeout = TimeSpan.FromSeconds(1);

        try
        {
            var result = await RunAsync(Workflow(Command("work", Tool("sleep"), CommandMode.Worker, inputs: ["**"])));

            Assert.Equal("worker did not answer 'hello' within 1 s", Assert.Single(result.Errors).Message);
        }
        finally
        {
            Douze.Workflow.Commands.WorkerProcess.HandshakeTimeout = timeout;
        }
    }
}
