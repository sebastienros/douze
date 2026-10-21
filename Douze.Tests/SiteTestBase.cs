using Douze.FileSystems;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow;
using Xunit;

namespace Douze.Tests;

/// <summary>
/// Builds a site from a <see cref="MemoryFileSystem"/> source to a <see cref="MemoryFileSystem"/> output.
/// </summary>
public abstract class SiteTestBase
{
    internal MemoryFileSystem Source { get; set; } = new();

    internal MemoryFileSystem Output { get; set; } = new();

    /// <summary>
    /// The site options; detected from <see cref="Source"/> when the build starts if not set.
    /// </summary>
    internal SiteOptions? Site { get; set; }

    /// <summary>
    /// Builds with <paramref name="workflow"/> (default: <see cref="DefaultWorkflow"/>), a silent view, no trace, and 4
    /// parallel units.
    /// </summary>
    internal async Task<BuildResult> BuildAsync(WorkflowDefinition? workflow = null, BuildOptions? options = null)
    {
        var site = Site ??= await SiteOptions.DetectAsync(Source, Output, null, null);

        return await SiteBuilder.BuildAsync(workflow ?? DefaultWorkflow.Create(site), site, options ?? new BuildOptions
        {
            MaxDegreeOfParallelism = 4,
            Trace = NullTraceRecorder.Instance,
            View = SilentProgressView.Discard,
            Cancellation = TestContext.Current.CancellationToken,
        });
    }

    /// <summary>
    /// Builds and fails the test with the errors when the build does not succeed.
    /// </summary>
    internal async Task<BuildResult> BuildSuccessfullyAsync(WorkflowDefinition? workflow = null)
    {
        var result = await BuildAsync(workflow);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result;
    }
}
