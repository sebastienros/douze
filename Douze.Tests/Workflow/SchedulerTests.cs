using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Xunit;

namespace Douze.Tests.Workflow;

public class SchedulerTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FPath[] Paths(IEnumerable<VirtualFile> files) => files.Select(x => x.Path).ToArray();

    [Fact]
    public async Task MapStreamsFilesToTheNextStep()
    {
        var site = await new FakeSite().WithFilesAsync("/a.txt", "/b.txt");
        var downstreamSawA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var graph = new FakeWorkflow()
            .Add("first", d => new FakeMapStep(d)
            {
                // b.txt only finishes once the next step has processed a.txt.
                Before = (file, ct) => file.Path == "/b.txt" ? downstreamSawA.Task.WaitAsync(GateTimeout, ct) : Task.CompletedTask,
            }, inputs: ["**"])
            .Add("second", d => new FakeMapStep(d)
            {
                Before = (file, ct) =>
                {
                    if (file.Path == "/a.txt")
                    {
                        downstreamSawA.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
            }, needs: ["first"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["/a.txt|first|second", "/b.txt|first|second"], await ContentsAsync(result["second"]));
    }

    [Fact]
    public async Task ConsumedFilesAreLoadedBeforeTheirStepSeesThem()
    {
        // Source files are read lazily; the scheduler loads a consumed file before its unit runs (map) or before the
        // barrier releases (reduce, group), so that neither the step nor the hash of its input refs waits on I/O.
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md", "/c.txt");
        var loadedAtMap = new ConcurrentQueue<(FPath Path, bool Loaded)>();
        FakeReduceStep? reduce = null;
        FakeGroupStep? group = null;

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                Process = (f, _, _) =>
                {
                    loadedAtMap.Enqueue((f.Path, f.Content.Length != null));
                    return new(FileList.Of(f));
                },
            }, inputs: ["**/*.md"])
            .Add("data", d => reduce = new FakeReduceStep(d), inputs: ["**/*.txt"])
            .Add("dirs", d => group = new FakeGroupStep(d), inputs: ["**/*.txt"], groupBy: "dir")
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(2, loadedAtMap.Count);
        Assert.All(loadedAtMap, x => Assert.True(x.Loaded, $"{x.Path} was not loaded"));
        Assert.All(reduce!.Received, x => Assert.NotNull(x.Content.Length));
        Assert.All(group!.Groups.SelectMany(x => x.Files), x => Assert.NotNull(x.Content.Length));
    }

    [Fact]
    public async Task ReduceSeesAllInputsSorted()
    {
        var site = await new FakeSite().WithFilesAsync("/c.txt", "/a.txt", "/b/1.txt", "/b.txt");
        FakeReduceStep? reduce = null;

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                // The first paths finish last.
                Before = (file, ct) => Task.Delay(file.Path == "/a.txt" ? 100 : file.Path == "/b.txt" ? 50 : 0, ct),
            }, inputs: ["**"])
            .Add("all", d => reduce = new FakeReduceStep(d), needs: ["pages"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["/a.txt", "/b.txt", "/b/1.txt", "/c.txt"], Paths(reduce!.Received).Select(x => x.FullName));

        var unit = Assert.Single(result["all"].Results);
        Assert.Equal(new UnitKey("all", "*"), unit.Key);
        Assert.Equal(["/a.txt", "/b.txt", "/b/1.txt", "/c.txt"], unit.Inputs.Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task GroupPartitionsAndOrdersByKey()
    {
        var site = await new FakeSite().WithFilesAsync("/b/1.txt", "/a/2.txt", "/a/1.txt", "/c.txt", "/a/3.md");

        var byDir = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f)) }, inputs: ["**"])
            .Add("dirs", d => new FakeGroupStep(d), needs: ["pages"], groupBy: "dir")
            .Build();

        var result = await site.BuildAsync(byDir);
        var run = result["dirs"];

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(StepShape.Group, run.Node.Step.EffectiveShape);
        Assert.Equal(["/", "/a/", "/b/"], run.Results.Select(x => x.Key.Unit));
        Assert.Equal(["/a/1.txt", "/a/2.txt", "/a/3.md"], run.Results[1].Inputs.Select(x => x.Path.FullName));
        Assert.Equal("/a/1.txt,/a/2.txt,/a/3.md", await run.Results[1].Outputs[0].Content.ReadTextAsync(Ct));
        Assert.Equal(3, run.Units);

        // Any other group-by is a Liquid template with the file in scope.
        var byExtension = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f)) }, inputs: ["**"])
            .Add("extensions", d => new FakeGroupStep(d), needs: ["pages"], groupBy: "{{ file.ext | remove: '.' }}")
            .Build();

        result = await site.BuildAsync(byExtension);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["md", "txt"], result["extensions"].Results.Select(x => x.Key.Unit));
        Assert.Equal(["/a/1.txt", "/a/2.txt", "/b/1.txt", "/c.txt"], result["extensions"].Results[1].Inputs.Select(x => x.Path.FullName));
    }

    [Fact]
    public async Task FanOutDeliversEveryFileToBothConsumers()
    {
        var site = await new FakeSite().WithFilesAsync("/a.txt", "/b.txt", "/c.txt");

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d), inputs: ["**"])
            .Add("left", d => new FakeMapStep(d), needs: ["pages"])
            .Add("right", d => new FakeMapStep(d), needs: ["pages"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["/a.txt|pages|left", "/b.txt|pages|left", "/c.txt|pages|left"], await ContentsAsync(result["left"]));
        Assert.Equal(["/a.txt|pages|right", "/b.txt|pages|right", "/c.txt|pages|right"], await ContentsAsync(result["right"]));
    }

    [Fact]
    public async Task DuplicatePathFromTwoProducersFails()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/a.json");

        static FakeMapStep ToIndex(StepDefinition d) => new(d)
        {
            Process = (f, _, _) => new(FileList.Of(f.With(d.Name, path: "/a/index.html"))),
        };

        var graph = new FakeWorkflow()
            .Add("render", ToIndex, inputs: ["**/*.md"])
            .Add("api", ToIndex, inputs: ["**/*.json"])
            .Write("write", "render", "api")
            .Build();

        var result = await site.BuildAsync(graph);

        var error = Assert.Single(result.Errors);
        Assert.Equal("step 'write' received '/a/index.html' from both 'api' and 'render'", error.Message);
        Assert.Equal("error: write: /a/index.html: step 'write' received '/a/index.html' from both 'api' and 'render'", error.ToString());
        Assert.Equal(StepState.Failed, result["write"].State);
    }

    [Fact]
    public async Task SameFileThroughTwoNeedsEntersOnce()
    {
        // A diamond: 'collect' consumes the Markdown files and emits nothing, and passes the stylesheet through, so the
        // same stylesheet reaches 'render' from 'pages' and from 'collect'. It is the same file, not a duplicate.
        var site = await new FakeSite().WithFilesAsync("/a.md", "/style.css");

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f)) }, inputs: ["**"])
            .Add("collect", d => new FakeReduceStep(d), needs: ["pages"], match: ["**/*.md"])
            .Add("render", d => new FakeMapStep(d), needs: ["pages", "collect"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(2, result["render"].FilesIn);
        Assert.Equal(["/a.md|render", "/style.css|render"], await ContentsAsync(result["render"]));
    }

    [Fact]
    public async Task UnmatchedFilesPassThroughImmediately()
    {
        var site = await new FakeSite().WithFilesAsync("/index.md", "/style.css");
        var cssArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<VirtualFile>();

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f)) }, inputs: ["**"])
            .Add("markdown", d => new FakeMapStep(d)
            {
                // The Markdown file is only processed once the stylesheet went past the step.
                Before = (_, ct) => cssArrived.Task.WaitAsync(GateTimeout, ct),
            }, needs: ["pages"], match: ["**/*.md"])
            .Add("sink", d => new FakeMapStep(d)
            {
                Process = (f, _, _) =>
                {
                    received.Enqueue(f);

                    if (f.Path == "/style.css")
                    {
                        cssArrived.TrySetResult();
                    }

                    return new(FileList.Of(f));
                },
            }, needs: ["markdown"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["/style.css", "/index.md"], received.Select(x => x.Path.FullName));

        // The passed through file is the same instance, untouched.
        var css = received.First();
        Assert.Same(Assert.Single(result["pages"].Results, x => x.Key.Unit == "/style.css").Outputs[0], css);
        Assert.Equal("/style.css", await css.Content.ReadTextAsync(Ct));

        var markdown = result["markdown"];
        Assert.Equal(2, markdown.FilesIn);
        Assert.Equal(1, markdown.FilesConsumed);
        Assert.Equal(1, markdown.FilesPassedThrough);
    }

    [Fact]
    public async Task DefaultPolicyStopsTheStepAndSkipsDependents()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md", "/c.md", "/d.md", "/x.txt");
        FakeMapStep? broken = null;

        var graph = new FakeWorkflow()
            .Add("broken", d => broken = new FakeMapStep(d) { FailWhen = f => f.Path == "/a.md" }, inputs: ["**/*.md"])
            .Add("after", d => new FakeMapStep(d), needs: ["broken"])
            .Add("other", d => new FakeMapStep(d) { Delay = TimeSpan.FromMilliseconds(50) }, inputs: ["**/*.txt"])
            .Build();

        // One slot: the first unit fails before any other starts.
        var result = await site.BuildAsync(graph, parallelism: 1);

        var error = Assert.Single(result.Errors);
        Assert.Equal("error: broken: /a.md: failed on /a.md", error.ToString());
        Assert.Equal(StepState.Failed, result["broken"].State);
        Assert.Equal(StepState.Skipped, result["after"].State);
        Assert.Equal(StepState.Succeeded, result["other"].State);
        Assert.Single(result["other"].Results);
        Assert.True(broken!.Started.Count < 4, $"the remaining units should be cancelled, {broken.Started.Count} started");
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task FailFastCancelsIndependentSteps()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/x.txt");
        var independentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var graph = new FakeWorkflow()
            .Add("broken", d => new FakeMapStep(d)
            {
                Before = (_, ct) => independentStarted.Task.WaitAsync(GateTimeout, ct),
                FailWhen = _ => true,
            }, inputs: ["**/*.md"])
            .Add("after", d => new FakeMapStep(d), needs: ["broken"])
            .Add("independent", d => new FakeMapStep(d)
            {
                Before = async (_, ct) =>
                {
                    independentStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                },
            }, inputs: ["**/*.txt"])
            .Build();

        var result = await site.BuildAsync(graph, FailurePolicy.FailFast);

        Assert.Single(result.Errors);
        Assert.Equal(StepState.Failed, result["broken"].State);
        Assert.Equal(StepState.Skipped, result["after"].State);
        Assert.Equal(StepState.Cancelled, result["independent"].State);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task KeepGoingReportsEveryError()
    {
        var site = await new FakeSite().WithFilesAsync("/e.md", "/d.md", "/c.md", "/b.md", "/a.md");
        FakeMapStep? step = null;

        var graph = new FakeWorkflow()
            .Add("pages", d => step = new FakeMapStep(d) { FailWhen = f => f.Path != "/b.md" && f.Path != "/d.md" }, inputs: ["**"])
            .Add("after", d => new FakeMapStep(d), needs: ["pages"])
            .Build();

        var result = await site.BuildAsync(graph, FailurePolicy.KeepGoing);

        Assert.Equal(["/a.md", "/c.md", "/e.md"], result.Errors.Select(x => x.File!.Value.FullName));
        Assert.Equal(["/b.md", "/d.md"], step!.Done.Select(x => x.FullName).Order());
        Assert.Equal(StepState.Failed, result["pages"].State);
        Assert.Equal(StepState.Skipped, result["after"].State);
        Assert.Equal(5, result["pages"].UnitsDone);
    }

    [Fact]
    public async Task SkipPropagatesTwoLevels()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md");

        var graph = new FakeWorkflow()
            .Add("broken", d => new FakeMapStep(d) { FailWhen = _ => true }, inputs: ["**"])
            .Add("child", d => new FakeMapStep(d), needs: ["broken"])
            .Add("grandchild", d => new FakeReduceStep(d), needs: ["child"])
            .Add("independent", d => new FakeMapStep(d), inputs: ["**"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.Equal(StepState.Failed, result["broken"].State);
        Assert.Equal(StepState.Skipped, result["child"].State);
        Assert.Equal(StepState.Skipped, result["grandchild"].State);
        Assert.Empty(result["grandchild"].Results);
        Assert.Equal(StepState.Succeeded, result["independent"].State);
    }

    [Fact]
    public async Task UserCancellationReturnsCancelled()
    {
        var site = await new FakeSite().WithFilesAsync("/a.md", "/b.md");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d)
            {
                Before = async (_, ct) =>
                {
                    await cts.CancelAsync();
                    await Task.Delay(Timeout.Infinite, ct);
                },
            }, inputs: ["**"])
            .Add("all", d => new FakeReduceStep(d), needs: ["pages"])
            .Build();

        var result = await site.BuildAsync(graph, cancellation: cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Errors);
        Assert.Equal(StepState.Cancelled, result["pages"].State);
        Assert.Equal(StepState.Cancelled, result["all"].State);
    }

    [Fact]
    public async Task ScopeContributionsMergeWithTheRightPrecedence()
    {
        var site = await new FakeSite().WithFilesAsync("/data.yml", "/blog/post.md");
        FakeReduceStep? near = null;

        static SiteScope Scope(string source, string title) => new(source, new NodeObject { ["title"] = NodeValue.Create(title), [source] = NodeValue.Create(title) });

        static string Resolve(StepContext context, Metadata meta, string key) =>
            context.Scopes.TryResolve(meta, key, null, context.Dependencies, out var value, out _) ? value!.GetValue<string>() : "-";

        var graph = new FakeWorkflow()
            .Add("data", d => new FakeReduceStep(d)
            {
                Reduce = (_, _) => new StepOutput(FileList.None, Scope("data", "data"), [new FolderScope("/blog/", new NodeObject { ["layout"] = NodeValue.Create("blog.html") }, ["/data.yml"])]),
            }, inputs: ["**/*.yml"])
            .Add("pages", d => new FakeMapStep(d)
            {
                Process = (f, c, _) => new(FileList.Of(f.With(d.Name, content: FileContent.FromText($"{Resolve(c, f.Metadata, "title")}")))),
            }, inputs: ["**/*.md"], needs: ["data"])
            .Add("near", d => near = new FakeReduceStep(d) { Reduce = (_, _) => new StepOutput(FileList.None, Scope("step:near", "near")) }, needs: ["pages"])
            .Add("render", d => new FakeMapStep(d)
            {
                Process = (f, c, _) => new(FileList.Of(f.With(d.Name, content: FileContent.FromText(
                    $"{Resolve(c, f.Metadata, "title")} {Resolve(c, f.Metadata, "data")} {Resolve(c, f.Metadata, "step:near")} {Resolve(c, f.Metadata, "layout")}")))),
            }, needs: ["pages", "near"])
            .Build();

        var result = await site.BuildAsync(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        // "pages" is not downstream of "near": it only sees the data.
        Assert.Equal(["data"], await ContentsAsync(result["pages"]));
        Assert.Equal(["data"], near!.SeenScopes!.Site.Select(x => x.Source));

        // The nearest contribution wins; the others are still reachable.
        Assert.Equal(["near data near blog.html"], await ContentsAsync(result["render"]));
        Assert.Equal(["step:near", "data"], result["render"].Scopes.Site.Select(x => x.Source));
        Assert.Equal(["step:near", "data"], result.Scopes.Site.Select(x => x.Source));
        Assert.Equal(["data"], result["data"].Contributions.Site.Select(x => x.Source));
        Assert.Equal(["data", "step:near"], result["render"].Results[0].Dependencies.Scopes);
        Assert.Equal(["/data.yml"], result["render"].Results[0].Dependencies.DataFiles.Select(x => x.FullName));
    }

    [Fact]
    public async Task NeverMoreThanNUnitsRunAtOnce()
    {
        var site = await new FakeSite().WithFilesAsync(Enumerable.Range(0, 20).Select(i => $"/{i:00}.txt").ToArray());
        var counter = new ConcurrencyCounter();

        FakeMapStep Map(StepDefinition d) => new(d) { Counter = counter, Delay = TimeSpan.FromMilliseconds(5) };

        var graph = new FakeWorkflow()
            .Add("one", Map, inputs: ["**"])
            .Add("two", d => new FakeMapStep(d)
            {
                Counter = counter,
                Delay = TimeSpan.FromMilliseconds(5),
                Process = (f, _, _) => new(FileList.Of(f.With(d.Name, path: "/two" + f.Path.FullName))),
            }, inputs: ["**"])
            .Add("three", Map, needs: ["one"])
            .Add("all", d => new FakeReduceStep(d) { Counter = counter }, needs: ["two", "three"])
            .Build();

        var result = await site.BuildAsync(graph, parallelism: 3);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(40, result["all"].FilesConsumed);
        Assert.InRange(counter.Max, 1, 3);
    }

    [Fact]
    public async Task SameGraphRunTwiceYieldsIdenticalOutputsAndResultKeys()
    {
        var paths = Enumerable.Range(0, 30).Select(i => $"/{(char)('a' + i % 3)}/{i:00}.txt").ToArray();

        var graph = new FakeWorkflow()
            .Add("pages", d => new FakeMapStep(d) { Before = (_, ct) => Task.Delay(Random.Shared.Next(0, 5), ct) }, inputs: ["**"])
            .Add("index", d => new FakeReduceStep(d)
            {
                Reduce = (files, _) => new StepOutput(
                    FileList.Of(VirtualFile.Derived(d.Name, files, "/index.txt", FileContent.FromText(string.Join("\n", files.Select(x => x.Path))), Metadata.Empty, null)),
                    new SiteScope("step:index", new NodeObject { ["count"] = NodeValue.Create(files.Count) })),
            }, needs: ["pages"])
            .Add("dirs", d => new FakeGroupStep(d), needs: ["pages"], groupBy: "dir")
            .Add("render", d => new FakeMapStep(d), needs: ["pages", "index"])
            .Write("write", "render", "dirs")
            .Build();

        async Task<(FakeSite Site, BuildResult Result)> BuildAsync()
        {
            var site = await new FakeSite().WithFilesAsync(paths);
            return (site, await site.BuildAsync(graph));
        }

        var (firstSite, first) = await BuildAsync();
        var (secondSite, second) = await BuildAsync();

        Assert.True(first.Succeeded, string.Join("\n", first.Errors));
        Assert.True(second.Succeeded, string.Join("\n", second.Errors));

        // End to end: every file reached the output file system through the write step.
        Assert.Equal(34, first.Outputs.Count);
        Assert.Equal(first.Outputs.Select(x => x.Ref), second.Outputs.Select(x => x.Ref));
        Assert.Equal("/a/00.txt|pages|render", await firstSite.Output.ReadAllTextAsync("/a/00.txt"));
        Assert.Equal(await firstSite.Output.ReadAllTextAsync("/groups/b.txt"), await secondSite.Output.ReadAllTextAsync("/groups/b.txt"));

        Assert.Equal(Keys(first), Keys(second));
        Assert.Equal(Dependencies(first), Dependencies(second));
        Assert.Equal(first.Scopes.Hash, second.Scopes.Hash);

        static string[] Keys(BuildResult result) => result.Steps
            .SelectMany(run => run.Results.Select(unit => ResultKey.Compute(run.Node.Definition, unit.Inputs, Hash128.Empty).ToString()))
            .ToArray();

        static DependencySet[] Dependencies(BuildResult result) => result.Steps.SelectMany(run => run.Results.Select(unit => unit.Dependencies)).ToArray();
    }

    [Fact]
    public async Task StepsCompleteAfterTheStepsTheyNeed()
    {
        // For every edge A -> B, A reaches its final state and is reported before B, whatever the states: a dependent
        // that fails early (shallow-fail) or is skipped is still reported after a slow need.
        var site = await new FakeSite().WithFilesAsync(Enumerable.Range(0, 8).Select(i => $"/{i}.md").ToArray());

        var workflow = new FakeWorkflow()
            .Add("slow", d => new FakeMapStep(d) { Before = (f, ct) => Task.Delay(f.Path == "/7.md" ? 30 : Random.Shared.Next(0, 3), ct) }, inputs: ["**"])
            .Add("fast", d => new FakeMapStep(d), needs: ["slow"])
            .Add("shallow-fail", d => new FakeMapStep(d) { FailWhen = _ => true }, needs: ["slow"])
            .Add("skipped", d => new FakeMapStep(d), needs: ["shallow-fail"])
            .Add("all", d => new FakeReduceStep(d), needs: ["fast", "skipped"])
            .Add("independent", d => new FakeMapStep(d) { Process = (f, _, _) => new(FileList.Of(f.With(d.Name, path: "/independent" + f.Path.FullName))) }, inputs: ["**"])
            .Write("write", "fast", "independent");

        var graph = workflow.Build();
        var edges = graph.Nodes.SelectMany(b => b.Needs.Select(a => (A: a.Definition.Name, B: b.Definition.Name))).ToArray();

        foreach (var policy in new[] { FailurePolicy.Default, FailurePolicy.KeepGoing, FailurePolicy.FailFast })
        {
            for (var i = 0; i < 20; i++)
            {
                var view = new RecordingView();
                var result = await site.BuildAsync(graph, policy, view: view);

                var states = $"{policy}: " + string.Join(", ", result.Steps.Select(x => $"{x.Node.Definition.Name}={x.State}")) + " | " + string.Join("; ", result.Errors);
                Assert.Equal(graph.Nodes.Count, view.Completed.Count);
                Assert.True(result["shallow-fail"].State == StepState.Failed, states);
                Assert.True(result["skipped"].State == StepState.Skipped, states);
                Assert.True(result["all"].State == StepState.Skipped, states);
                Assert.True(result.Errors.Count > 0 && result.Errors.All(x => x.Step == "shallow-fail"), states);

                foreach (var (a, b) in edges)
                {
                    var completed = view.Completed.Select(x => x.Name).ToList();
                    Assert.True(completed.IndexOf(a) < completed.IndexOf(b), $"{policy}: '{b}' completed before '{a}': {string.Join(", ", completed)}");
                }

                // The view saw final runs: the state and end time it observed are the ones of the result.
                foreach (var (name, state, ended) in view.Completed)
                {
                    Assert.Equal(result[name].State, state);
                    Assert.Equal(result[name].EndedAt, ended);
                    Assert.NotEqual(StepState.Running, state);
                }
            }
        }
    }

    private sealed class RecordingView : Douze.Progress.IProgressView
    {
        private readonly Lock _lock = new();

        public List<(string Name, StepState State, DateTimeOffset? Ended)> Completed { get; } = [];

        public void StepCompleted(StepRun run)
        {
            lock (_lock)
            {
                Completed.Add((run.Node.Definition.Name, run.State, run.EndedAt));
            }
        }

        public void BuildStarted(WorkflowGraph graph) { }
        public void StepStarted(StepRun run) { }
        public void StepProgress(StepRun run) { }
        public void Message(Douze.Progress.LogLevel level, string? step, string message) { }
        public void BuildCompleted(BuildResult result) { }
        public void Dispose() { }
    }

    private static async Task<string[]> ContentsAsync(StepRun run)
    {
        var contents = new List<string>();

        foreach (var unit in run.Results)
        {
            foreach (var file in unit.Outputs)
            {
                contents.Add(await file.Content.ReadTextAsync(Ct));
            }
        }

        return [.. contents];
    }
}
