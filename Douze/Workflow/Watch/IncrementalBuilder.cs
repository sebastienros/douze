using Douze.FileSystems;
using Douze.Liquid;
using Douze.Progress;
using Douze.Workflow.Cache;

namespace Douze.Workflow.Watch;

/// <summary>
/// The result of a build of a watch session: the build, and the output files it changed.
/// </summary>
/// <param name="Build">The build.</param>
/// <param name="Changed">The output files the build wrote (its units that ran, not the replayed ones) or deleted, sorted.</param>
internal sealed record RebuildResult(BuildResult Build, IReadOnlyList<FPath> Changed);

/// <summary>
/// Builds a site again and again as its sources change (watch mode). Every build runs the whole graph through the
/// scheduler, which replays each unit whose result key and dependencies did not change and runs the others; early
/// cutoff stops the propagation of a change whose outputs are the same. Across builds the session keeps the disk cache
/// open (its results are committed in memory after each build and written once, when the session ends), and the
/// template loader, from which the changed files are invalidated. The workflow and the site options are loaded again
/// for every build: an edited workflow file, or a site folder that appeared, takes effect at once.
/// </summary>
internal sealed class IncrementalBuilder : IDisposable
{
    private readonly Func<Task<(WorkflowDefinition Workflow, SiteOptions Site)>> _load;
    private readonly BuildOptions _options;
    private readonly IProgressView _sessionView;

    private WorkflowGraph? _graph;
    private string? _workflowYaml;
    private SiteOptions? _site;
    private TemplateLoader? _templates;
    private BuildCache? _cache;
    private bool _cacheOpened;
    private IReadOnlyList<(FPath Path, long Length, DateTime LastWriteTimeUtc)>? _previousWritten;
    private SourceSnapshot? _sources;

    /// <param name="load">Loads the workflow and the site options.</param>
    /// <param name="options">The options of every build; its view, cache, templates and cancellation are replaced.</param>
    /// <param name="sessionView">Where the messages of the cache go, for the whole session.</param>
    public IncrementalBuilder(Func<Task<(WorkflowDefinition Workflow, SiteOptions Site)>> load, BuildOptions options, IProgressView sessionView)
    {
        _load = load;
        _options = options;
        _sessionView = sessionView;
    }

    /// <summary>
    /// The site options of the last build that could load its workflow.
    /// </summary>
    public SiteOptions? Site => _site;

    /// <summary>
    /// The graph of the last build that could load its workflow.
    /// </summary>
    public WorkflowGraph? Graph => _graph;

    /// <summary>
    /// The template loader the builds share.
    /// </summary>
    internal TemplateLoader? Templates => _templates;

    /// <summary>
    /// Builds the site: the first time when <paramref name="changes"/> is null, then after the changes. Returns null when
    /// the workflow cannot be loaded (its errors are reported to <paramref name="view"/>): the next build tries again.
    /// </summary>
    public async Task<RebuildResult?> BuildAsync(ChangeSet? changes, IProgressView view, CancellationToken ct)
    {
        try
        {
            return await BuildCoreAsync(changes, view, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // What the session kept may not match the source folder anymore: the next build scans it.
            _sources = null;
            _templates?.InvalidateAll();
            throw;
        }
    }

    private async Task<RebuildResult?> BuildCoreAsync(ChangeSet? changes, IProgressView view, CancellationToken ct)
    {
        if (!await LoadAsync(view))
        {
            return null;
        }

        var site = _site!;

        if (changes != null)
        {
            Invalidate(changes);

            // The source files that did not change keep their stats; after lost events, the build scans them all.
            if (changes.Rescan)
            {
                _sources = null;
            }
            else if (_sources != null)
            {
                foreach (var (path, kind) in await _sources.ApplyAsync(site.Source, changes.Changes.Select(x => x.Path)))
                {
                    changes.Correct(path, kind);
                }
            }
        }

        _templates!.Trim();

        if (!_cacheOpened)
        {
            _cacheOpened = true;
            _cache = OpenCache(site);
        }

        var options = new BuildOptions
        {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            Failure = _options.Failure,
            Verbose = _options.Verbose,
            TraceUnits = _options.TraceUnits,
            TracePath = _options.TracePath,
            TimelinePath = _options.TimelinePath,
            View = view,
            Trace = _options.Trace,
            CacheDirectory = _options.CacheDirectory,
            UseCache = _cache != null,
            KeepFiles = _options.KeepFiles,
            Cache = _cache,
            Templates = _templates,
            PreviousWritten = _cache == null ? _previousWritten : null,
            RecordWritten = true,
            Sources = _sources,
            Cancellation = ct,
        };

        var result = await SiteBuilder.BuildAsync(_graph!, site, options);
        _sources = result.Sources;

        // Without the cache, what this build wrote is what the next one may delete; after a failed build, what the
        // builds before it wrote too.
        _previousWritten = result.Succeeded || _previousWritten == null ? result.Written : [.. _previousWritten, .. result.Written];

        return new RebuildResult(result, Changed(result));
    }

    /// <summary>
    /// Loads the workflow and the site options, and keeps the previous graph (its steps, its template loader) when they
    /// did not change.
    /// </summary>
    private async Task<bool> LoadAsync(IProgressView view)
    {
        WorkflowGraph graph;
        SiteOptions site;

        try
        {
            (var workflow, site) = await _load();
            graph = WorkflowGraph.Build(workflow);
        }
        catch (WorkflowValidationException e)
        {
            foreach (var error in e.Errors)
            {
                view.Message(LogLevel.Error, null, error.ToString());
            }

            return false;
        }

        var yaml = graph.ToYaml();

        if (_graph != null && yaml == _workflowYaml && SameSite(site, _site!))
        {
            return true;
        }

        if (_site == null || _templates == null || site.IncludesFolder != _site.IncludesFolder)
        {
            _templates = new TemplateLoader(site.Source, [site.IncludesFolder], LiquidOptions.CreateParser());
        }

        // Another site (a folder moved): its sources are scanned again.
        if (_site != null && !SameSite(site, _site))
        {
            _sources = null;
        }

        _graph = graph;
        _workflowYaml = yaml;
        _site = site;
        return true;
    }

    private static bool SameSite(SiteOptions a, SiteOptions b)
    {
        return a.SourceDirectory == b.SourceDirectory && a.OutputDirectory == b.OutputDirectory
            && a.ContentFolder == b.ContentFolder && a.DataFolder == b.DataFolder && a.IncludesFolder == b.IncludesFolder
            && a.Url == b.Url && a.Parallelism == b.Parallelism;
    }

    /// <summary>
    /// Forgets the templates that changed. A changed path may be a folder: its files are forgotten too.
    /// </summary>
    private void Invalidate(ChangeSet changes)
    {
        if (changes.Rescan)
        {
            _templates!.InvalidateAll();
            return;
        }

        foreach (var (path, _) in changes.Changes)
        {
            _templates!.Invalidate(path);
            _templates.Invalidate(new FPath(path.FullName + "/"));
        }
    }

    /// <summary>
    /// Whether a change does not start a build: in <c>.git</c>, and in the output and cache folders when they are inside
    /// the source folder, which the builds write. Called by the watcher's threads.
    /// </summary>
    public bool IsIgnored(FPath path)
    {
        if (path.FullName == "/.git" || path.FullName.StartsWith("/.git/", StringComparison.Ordinal))
        {
            return true;
        }

        if (_site is not { } site)
        {
            return false;
        }

        var cache = _options.CacheDirectory ?? (site.SourceDirectory is { } source ? Path.Combine(source, BuildContext.DefaultCacheFolder) : null);

        foreach (var folder in new[] { BuildContext.InsideSource(site, site.OutputDirectory), BuildContext.InsideSource(site, cache) })
        {
            if (folder is { } excluded && (path.IsInDirectory(excluded) || path.FullName + "/" == excluded.FullName))
            {
                return true;
            }
        }

        return false;
    }

    private BuildCache? OpenCache(SiteOptions site)
    {
        if (!_options.UseCache)
        {
            return null;
        }

        var directory = _options.CacheDirectory ?? (site.SourceDirectory is { } source ? Path.Combine(source, BuildContext.DefaultCacheFolder) : null);

        return directory == null ? null : BuildCache.Open(Path.Combine(Path.GetFullPath(directory), "cache"), _sessionView);
    }

    /// <summary>
    /// The output files that the units that ran wrote, and the deleted ones.
    /// </summary>
    private static IReadOnlyList<FPath> Changed(BuildResult result)
    {
        return [.. result.Steps
            .SelectMany(x => x.Results)
            .Where(x => !x.Replayed)
            .SelectMany(x => x.Dependencies.Written.Select(w => w.Path))
            .Concat(result.Deleted)
            .Distinct()
            .Order()];
    }

    /// <summary>
    /// Writes the cache index and releases the cache.
    /// </summary>
    public void Dispose()
    {
        if (_cache != null)
        {
            _cache.Persist();
            _cache.Dispose();
            _cache = null;
        }
    }
}
