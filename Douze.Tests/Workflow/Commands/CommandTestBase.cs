using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Douze.Nodes;
using Douze.Progress;
using Douze.Tests.Workflow.Steps;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Xunit;

namespace Douze.Tests.Workflow.Commands;

/// <summary>
/// Runs command steps against <c>Douze.Tests.FakeTool</c>, started with <c>dotnet &lt;its dll&gt;</c>, so the tests need no
/// tool besides the SDK. Each test gets its own temporary folder, used as the cache folder of the build.
/// </summary>
public abstract class CommandTestBase : StepTestBase, IDisposable
{
    protected CommandTestBase()
    {
        TempFolder = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempFolder);
    }

    /// <summary>
    /// A folder deleted after the test.
    /// </summary>
    protected string TempFolder { get; }

    /// <summary>
    /// The messages the steps logged during the last build.
    /// </summary>
    internal ConcurrentQueue<(LogLevel Level, string? Step, string Message)> Messages { get; } = new();

    /// <summary>
    /// The path of the fake tool, built next to the tests: <c>Douze.Tests.FakeTool/bin/&lt;Configuration&gt;/net10.0/</c>.
    /// </summary>
    protected static string FakeToolPath { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Douze.Tests.FakeTool", "bin",
        typeof(CommandTestBase).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
        "net10.0", "Douze.Tests.FakeTool.dll"));

    /// <summary>
    /// A command line that runs the fake tool. The arguments are written as they are: quote them if needed.
    /// </summary>
    protected static string Tool(string mode, params string[] arguments)
    {
        return $"dotnet \"{FakeToolPath}\" {mode} {string.Join(' ', arguments)}".TrimEnd();
    }

    internal static StepDefinition Command(
        string name,
        string run,
        CommandMode mode,
        string[]? needs = null,
        string[]? inputs = null,
        string[]? match = null,
        string? groupBy = null,
        string[]? outputs = null,
        (string Key, string Value)[]? env = null,
        TimeSpan? timeout = null,
        StderrPolicy stderr = StderrPolicy.Fail,
        bool shell = true,
        NodeObject? with = null,
        string? cwd = null)
    {
        return new StepDefinition
        {
            Name = name,
            Run = run,
            Mode = mode,
            Needs = [.. needs ?? []],
            Inputs = [.. inputs ?? []],
            Match = [.. match ?? []],
            GroupBy = groupBy,
            Outputs = [.. outputs ?? []],
            Env = (env ?? []).ToImmutableDictionary(x => x.Key, x => x.Value),
            Timeout = timeout,
            Stderr = stderr,
            Shell = shell,
            With = with ?? new NodeObject(),
            WorkingDirectory = cwd,
        };
    }

    /// <summary>
    /// Builds with a view that records messages, the test's folder as the cache folder, and 4 parallel units.
    /// </summary>
    internal Task<BuildResult> RunAsync(WorkflowDefinition workflow, int parallelism = 4, bool verbose = false, FailurePolicy failure = FailurePolicy.Default, ITraceRecorder? trace = null)
    {
        Messages.Clear();

        return BuildAsync(workflow, new BuildOptions
        {
            MaxDegreeOfParallelism = parallelism,
            Verbose = verbose,
            Failure = failure,
            CacheDirectory = Path.Combine(TempFolder, "cache"),
            Trace = trace ?? NullTraceRecorder.Instance,
            View = new RecordingView(Messages),
            Cancellation = TestContext.Current.CancellationToken,
        });
    }

    internal async Task<BuildResult> RunSuccessfullyAsync(WorkflowDefinition workflow, int parallelism = 4, bool verbose = false)
    {
        var result = await RunAsync(workflow, parallelism, verbose);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result;
    }

    /// <summary>
    /// The text of the file a step emitted at <paramref name="path"/>.
    /// </summary>
    internal static Task<string> OutputTextAsync(StepRun run, string path) => TextAsync(Outputs(run).Single(x => x.Path == path));

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempFolder, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class RecordingView(ConcurrentQueue<(LogLevel, string?, string)> messages) : IProgressView
    {
        public void BuildStarted(WorkflowGraph graph) { }
        public void StepStarted(StepRun run) { }
        public void StepProgress(StepRun run) { }
        public void StepCompleted(StepRun run) { }
        public void Message(LogLevel level, string? step, string message) => messages.Enqueue((level, step, message));
        public void BuildCompleted(BuildResult result) { }
        public void Dispose() { }
    }
}
