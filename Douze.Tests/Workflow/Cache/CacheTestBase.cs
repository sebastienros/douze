using System.Collections.Concurrent;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

/// <summary>
/// Builds sites from a memory file system with a disk cache in a temporary folder, deleted after the test.
/// </summary>
public abstract class CacheTestBase : SiteTestBase, IDisposable
{
    internal string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "douze-tests", Guid.NewGuid().ToString("N"));

    internal ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

    /// <summary>
    /// Builds with the disk cache (unless <paramref name="cache"/> is false) and fails the test when the build fails.
    /// </summary>
    internal async Task<BuildResult> BuildCachedAsync(WorkflowDefinition? workflow = null, bool cache = true)
    {
        var result = await BuildAsync(workflow, Options(cache));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors.Select(x => $"{x}\n{x.Exception}")));
        return result;
    }

    internal BuildOptions Options(bool cache = true) => new()
    {
        MaxDegreeOfParallelism = 4,
        KeepFiles = true,
        CacheDirectory = CacheDirectory,
        UseCache = cache,
        Trace = NullTraceRecorder.Instance,
        View = new MessageView(Messages),
        Cancellation = TestContext.Current.CancellationToken,
    };

    /// <summary>
    /// The units of a step that ran instead of being replayed from the cache, sorted.
    /// </summary>
    internal static string[] Executed(BuildResult result, string step)
    {
        return [.. result[step].Results.Where(x => !x.Replayed).Select(x => x.Key.Unit).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The steps that ran at least one unit, in topological order.
    /// </summary>
    internal static string[] StepsThatRan(BuildResult result)
    {
        return [.. result.Steps.Where(x => x.UnitsExecuted > 0).Select(x => x.Node.Definition.Name)];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(CacheDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal sealed class MessageView(ConcurrentQueue<(LogLevel, string)> messages) : IProgressView
    {
        public void BuildStarted(WorkflowGraph graph) { }
        public void StepStarted(StepRun run) { }
        public void StepProgress(StepRun run) { }
        public void StepCompleted(StepRun run) { }
        public void Message(LogLevel level, string? step, string message) => messages.Enqueue((level, message));
        public void BuildCompleted(BuildResult result) { }
        public void Dispose() { }
    }
}
