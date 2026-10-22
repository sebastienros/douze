using Douze.FileSystems;
using Douze.Liquid;
using Douze.Progress;
using Douze.Tracing;
using Douze.Workflow.Cache;

namespace Douze.Workflow;

internal sealed class BuildOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public FailurePolicy Failure { get; init; } = FailurePolicy.Default;
    public bool Verbose { get; init; }
    public bool TraceUnits { get; init; }
    public string? TracePath { get; init; }          // --trace <file.json>
    public string? TimelinePath { get; init; }       // --timeline <file.html>
    public IProgressView? View { get; init; }        // null: chosen by Program; SiteBuilder falls back to a silent view
    public ITraceRecorder? Trace { get; init; }      // null: a TraceRecorder honoring TraceUnits
    public string? CacheDirectory { get; init; }     // default: <source>/.douze/
    public bool UseCache { get; init; } = true;      // --no-cache
    public bool KeepFiles { get; init; }             // keeps the output files of every unit in UnitResult.Files (tests)
    public BuildCache? Cache { get; init; }          // a cache kept open across builds (watch mode): committed, not saved, not disposed
    public TemplateLoader? Templates { get; init; }  // a template loader kept across builds (watch mode), invalidated by its owner
    public IReadOnlyList<(FPath Path, long Length, DateTime LastWriteTimeUtc)>? PreviousWritten { get; init; }  // what the previous build wrote, when there is no cache to tell
    public CancellationToken Cancellation { get; init; }
}
