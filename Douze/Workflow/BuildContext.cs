using Douze.Progress;
using Douze.Liquid;
using Douze.Tracing;
using Douze.FileSystems;
using Douze.Nodes;
using Fluid;

namespace Douze.Workflow;

/// <summary>
/// Services shared by every step of a build.
/// </summary>
internal sealed class BuildContext : IAsyncDisposable
{
    public BuildContext(SiteOptions site, BuildOptions options, IProgressView view, ITraceRecorder trace)
    {
        Site = site;
        Options = options;
        View = view;
        Trace = trace;
        Cancellation = options.Cancellation;

        var parallelism = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        MaxDegreeOfParallelism = parallelism;
        Parallelism = new SemaphoreSlim(parallelism, parallelism);
        ProcessSlots = new SemaphoreSlim(parallelism, parallelism);

        Templates = new TemplateLoader(site.Source, [site.IncludesFolder], Liquid.LiquidOptions.CreateParser());
        LiquidOptions = Liquid.LiquidOptions.Create(Templates);

        var cacheDirectory = options.CacheDirectory
            ?? Path.Combine(site.SourceDirectory ?? Path.GetTempPath(), ".douze");
        TempRoot = Path.Combine(cacheDirectory, "tmp", Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;

        BaseScopes = ScopeSet.FromSettings(SiteValues(site));
    }

    /// <summary>
    /// What templates see of the site settings, under <c>site</c>: <c>url</c> when it is set. The other settings
    /// (folders, output, parallelism) configure the build and are not exposed.
    /// </summary>
    private static NodeObject SiteValues(SiteOptions site)
    {
        if (site.Url == null)
        {
            return new NodeObject();
        }

        return new NodeObject { ["site"] = new NodeObject { ["url"] = NodeValue.Create(site.Url) } };
    }

    public SiteOptions Site { get; }
    public BuildOptions Options { get; }
    public IFileSystem Source => Site.Source;
    public IFileSystem Output => Site.Output;
    public TemplateLoader Templates { get; }        // shared parsed template cache + include file provider
    public TemplateOptions LiquidOptions { get; }   // built once by LiquidOptions.Create
    public ITraceRecorder Trace { get; }
    public IProgressView View { get; }
    public int MaxDegreeOfParallelism { get; }
    public SemaphoreSlim Parallelism { get; }       // CPU work slots for built-in steps
    public SemaphoreSlim ProcessSlots { get; }      // slots for external processes (command steps)
    public CancellationToken Cancellation { get; }  // user cancellation (Ctrl+C)
    public string TempRoot { get; }                 // <cache dir>/tmp/<build id>/ for batch commands, created on demand, deleted by DisposeAsync
    public ScopeSet BaseScopes { get; }             // the site settings templates see (site.url), below every contribution

    /// <summary>
    /// Releases the slots and deletes <see cref="TempRoot"/>, unless <c>--verbose</c> asked to keep what the batch
    /// commands left in it (their units delete their own folders otherwise).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Parallelism.Dispose();
        ProcessSlots.Dispose();

        try
        {
            Directory.Delete(TempRoot, recursive: !Options.Verbose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never created, kept with --verbose (not empty), or a file still open by a process that outlived its
            // command: the next build uses another folder.
        }

        return default;
    }
}
