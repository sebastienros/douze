using Douze.FileSystems;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Douze.Tests.Workflow.Cache;
using Xunit;

namespace Douze.Tests.Workflow;

/// <summary>
/// The channels in front of map steps are bounded: a producer waits for its slowest consumer. Each test would deadlock
/// without one of the rules that keep the waits finite (a unit writes its outputs without its slot, a node that stops
/// early drains its inputs, a channel in front of a step waiting for a barrier fed by the producer holds everything), so
/// each build runs under a timeout that fails the test instead of hanging it.
/// </summary>
public class BackpressureTests : CacheTestBase
{
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Files(int count, string extension = "txt") => [.. Enumerable.Range(0, count).Select(i => $"/{i:000}.{extension}")];

    private static Task<BuildResult> WithinTimeout(Task<BuildResult> build) => build.WaitAsync(DeadlockTimeout, Ct);

    private static FakeMapStep Identity(StepDefinition d) => new(d) { Process = (f, _, _) => new(FileList.Of(f.With(d.Name))) };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ASlowConsumerHoldsBackItsProducer(int parallelism)
    {
        // The producer gets at most a few files per slot ahead of a consumer that takes longer per file.
        var site = await new FakeSite().WithFilesAsync(Files(200));
        var produced = 0;
        var consumed = 0;
        var ahead = 0;

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                Process = (f, _, _) =>
                {
                    Interlocked.Increment(ref produced);
                    return new(FileList.Of(f.With(d.Name)));
                },
            }, inputs: ["**"])
            .Add("slow", d => new FakeMapStep(d)
            {
                Before = async (_, ct) =>
                {
                    var started = Interlocked.Increment(ref consumed);
                    var lead = Volatile.Read(ref produced) - started;

                    int max;
                    while (lead > (max = Volatile.Read(ref ahead)) && Interlocked.CompareExchange(ref ahead, lead, max) != max)
                    {
                    }

                    await Task.Delay(1, ct);
                },
            }, needs: ["pages"])
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: parallelism));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(200, result["slow"].Results.Count);

        // Ahead of the consumer's running units: its channel (2 per slot), its queue (1 per slot), its units waiting for
        // a slot, the file its reader holds, and the producer's units waiting to write. Unbounded, it was most files.
        Assert.InRange(ahead, 0, 6 * parallelism);
    }

    [Fact]
    public async Task AUnitWritesItsOutputsWithoutItsSlot()
    {
        // A unit with more outputs than its consumer's channel and queue hold (a paginated page): with one slot, holding
        // it while waiting for room would deadlock, since the consumer needs the slot to make room.
        var site = await new FakeSite().WithFilesAsync(Files(5));

        var graph = new FakeWorkflow()
            .Add("paginate", d => new FakeMapStep(d)
            {
                Process = (f, _, _) => new(FileList.Of([.. Enumerable.Range(0, 50).Select(i => f.With(d.Name, path: $"{f.Path.FullName}/{i:00}.txt"))])),
            }, inputs: ["**"])
            .Add("render", d => new FakeMapStep(d), needs: ["paginate"])
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: 1));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(250, result["render"].Results.Count);
    }

    [Fact]
    public async Task AStepThatStopsEarlyDrainsItsInputs()
    {
        // 'broken' fails on its first file and stops reading 'pages'; 'joined' is skipped when 'broken' fails and stops
        // reading 'ok'. Unless both drain what their needs still write, 'pages' and 'ok' wait forever on a full channel,
        // and so does 'slow', the other consumer of 'pages'.
        var site = await new FakeSite().WithFilesAsync(Files(100));

        var graph = new FakeWorkflow()
            .Add("pages", Identity, inputs: ["**"])
            .Add("broken", d => new FakeMapStep(d) { FailWhen = _ => true }, needs: ["pages"])
            .Add("after", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(1) }, needs: ["broken"])
            .Add("ok", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f.With(d.Name, path: "/ok" + f.Path.FullName))) }, needs: ["pages"])
            .Add("joined", d => new FakeMapStep(d), needs: ["broken", "ok"])
            .Add("slow", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(1) }, needs: ["pages"])
            .Build();

        foreach (var policy in new[] { FailurePolicy.Default, FailurePolicy.KeepGoing })
        {
            var result = await WithinTimeout(site.BuildAsync(graph, policy, parallelism: 1));

            Assert.Equal(StepState.Failed, result["broken"].State);
            Assert.Equal(StepState.Skipped, result["after"].State);
            Assert.Equal(StepState.Skipped, result["joined"].State);
            Assert.Equal(StepState.Succeeded, result["ok"].State);
            Assert.Equal(StepState.Succeeded, result["slow"].State);
            Assert.Equal(100, result["slow"].Results.Count);
        }
    }

    [Fact]
    public async Task FanOutWaitsForTheSlowestConsumer()
    {
        // 'pages' writes every file to both consumers, so the fast one cannot get further ahead of the slow one than
        // what the slow one's channel and queue hold.
        var site = await new FakeSite().WithFilesAsync(Files(200));
        var fastStarted = 0;
        var slowStarted = 0;
        var ahead = 0;

        var graph = new FakeWorkflow()
            .Add("pages", Identity, inputs: ["**"])
            .Add("fast", d => new FakeMapStep(d)
            {
                Before = (_, _) =>
                {
                    var lead = Interlocked.Increment(ref fastStarted) - Volatile.Read(ref slowStarted);

                    int max;
                    while (lead > (max = Volatile.Read(ref ahead)) && Interlocked.CompareExchange(ref ahead, lead, max) != max)
                    {
                    }

                    return Task.CompletedTask;
                },
            }, needs: ["pages"])
            .Add("slow", d => new FakeMapStep(d)
            {
                Before = (_, ct) =>
                {
                    Interlocked.Increment(ref slowStarted);
                    return Task.Delay(2, ct);
                },
            }, needs: ["pages"])
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: 2));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(200, result["fast"].Results.Count);
        Assert.Equal(200, result["slow"].Results.Count);
        Assert.InRange(ahead, 0, 6 * 2);
    }

    [Fact]
    public async Task ADiamondOfMapStepsStreams()
    {
        // 'render' gets every file from 'pages' and an equal copy from 'copy': each enters once.
        var site = await new FakeSite().WithFilesAsync(Files(100));

        var graph = new FakeWorkflow()
            .Add("pages", Identity, inputs: ["**"])
            .Add("copy", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f.With(d.Name))) }, needs: ["pages"])
            .Add("render", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(1) }, needs: ["pages", "copy"])
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: 1));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(100, result["render"].FilesIn);
        Assert.Equal(100, result["render"].Results.Count);
    }

    [Fact]
    public async Task AStepAfterABarrierDoesNotHoldBackTheBarrier()
    {
        // 'render' reads nothing before 'collect' published its contribution, and 'collect' needs every file of 'pages'.
        // Bounded, the channels from 'pages' (which also feeds 'collect') and from 'collect' (which passes the
        // stylesheets through while it reads) would fill before 'render' starts, and 'collect' would never complete.
        var site = await new FakeSite().WithFilesAsync([.. Files(100, "md"), .. Files(100, "css")]);

        var graph = new FakeWorkflow()
            .Add("pages", Identity, inputs: ["**"])
            .Add("collect", d => new FakeReduceStep(d), needs: ["pages"], match: ["**/*.md"])
            .Add("render", d => new FakeMapStep(d), needs: ["pages", "collect"])
            .Add("group", d => new FakeGroupStep(d), needs: ["render"], groupBy: "dir")
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: 1));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(100, Assert.Single(result["collect"].Results).Inputs.Length);
        Assert.Equal(200, result["render"].FilesIn);
        Assert.Equal(200, result["group"].FilesConsumed);
    }

    [Fact]
    public async Task CancellationReleasesBlockedProducers()
    {
        var site = await new FakeSite().WithFilesAsync(Files(100));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;

        var graph = new FakeWorkflow()
            .Add("pages", Identity, inputs: ["**"])
            .Add("slow", d => new FakeMapStep(d)
            {
                Before = async (_, ct) =>
                {
                    // By the third file, 'pages' waits on the full channel.
                    if (Interlocked.Increment(ref seen) == 3)
                    {
                        await Task.Delay(50, ct);
                        await cts.CancelAsync();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                },
            }, needs: ["pages"])
            .Add("all", d => new FakeReduceStep(d), needs: ["slow"])
            .Build();

        var result = await WithinTimeout(site.BuildAsync(graph, parallelism: 1, cancellation: cts.Token));

        Assert.True(result.Cancelled);
        Assert.Empty(result.Errors);
        Assert.Equal(StepState.Cancelled, result["pages"].State);
        Assert.Equal(StepState.Cancelled, result["slow"].State);
        Assert.Equal(StepState.Cancelled, result["all"].State);
    }

    [Fact]
    public async Task ReplayedUnitsStreamThroughBoundedChannels()
    {
        // The default workflow with one slot, so every bounded channel fills: a clean build, a build replaying every unit
        // from the cache, and a build after an edit, which mixes replayed and executed units.
        await Source.WriteTextFileAsync("/_includes/layout.html", "<html><nav>{% for p in collections.all limit: 3 %}{{ p.data.title }} {% endfor %}</nav>{{ content }}</html>");
        await Source.WriteTextFileAsync("/_data/layout.json", "\"layout.html\"");

        for (var i = 0; i < 60; i++)
        {
            await Source.WriteTextFileAsync($"/_content/posts/{i:00}.md", $"---\ntitle: Post {i}\ndate: 2020-01-01\ntags: [post]\n---\nbody {i}");
            await Source.WriteTextFileAsync($"/_content/assets/{i:00}.css", $"/* {i} */");
        }

        var clean = await WithinTimeout(BuildCachedAsync(parallelism: 1));
        var replayed = await WithinTimeout(BuildCachedAsync(parallelism: 1));

        Assert.Equal(0, replayed.UnitsExecuted);
        Assert.Equal(clean.Outputs, replayed.Outputs);

        await Source.WriteTextFileAsync("/_content/posts/30.md", "---\ntitle: Post 30\ndate: 2020-01-01\ntags: [post]\n---\nedited");
        var edited = await WithinTimeout(BuildCachedAsync(parallelism: 1));

        Assert.InRange(edited.UnitsExecuted, 1, clean.UnitsExecuted - 1);
        Assert.Equal(clean.Outputs.Count, edited.Outputs.Count);
        Assert.Contains("edited", await Output.ReadAllTextAsync("/posts/30/index.html"));
    }
}
