using System.Collections.Immutable;
using Douze.Progress;
using Douze.Workflow.Cache;
using Douze.Liquid;
using Douze.Liquid.Tags;
using Douze.Images;
using Douze.Markdown;
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
    /// <summary>
    /// Creates the context of a build: scans the shortcodes, whose tags every template of the build is parsed with,
    /// before creating the template loader.
    /// </summary>
    /// <exception cref="WorkflowValidationException">A shortcode is invalid: the build does not start.</exception>
    public static async Task<BuildContext> CreateAsync(SiteOptions site, BuildOptions options, IProgressView view, ITraceRecorder trace, CancellationToken ct = default)
    {
        // Watch mode reuses a template loader across rebuilds: the shortcodes are registered on its parser.
        var parser = options.Templates?.Parser as DouzeParser ?? Liquid.LiquidOptions.CreateParser();
        var shortcodes = await ShortcodeRegistry.ScanAsync(site.Source, site.ShortcodesFolder, parser, ct);

        return new BuildContext(site, options, view, trace, parser, shortcodes);
    }

    private BuildContext(SiteOptions site, BuildOptions options, IProgressView view, ITraceRecorder trace, DouzeParser parser, ShortcodeRegistry shortcodes)
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

        Shortcodes = shortcodes;
        Templates = options.Templates ?? new TemplateLoader(site.Source, [site.IncludesFolder], parser);
        LiquidOptions = Liquid.LiquidOptions.Create(Templates);
        Markdown = new MarkdownRenderer(site.Markdown, site.Source, site.ContentFolder);

        // A site that is not on disk (tests) has no cache unless a folder is given; its commands use the temp folder.
        var cacheDirectory = options.CacheDirectory ?? (site.SourceDirectory is { } source ? Path.Combine(source, DefaultCacheFolder) : null);
        CacheRoot = cacheDirectory == null ? null : Path.GetFullPath(cacheDirectory);
        TempRoot = Path.Combine(CacheRoot ?? Path.Combine(Path.GetTempPath(), DefaultCacheFolder), "tmp", Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;

        // The output and cache folders, and .douze/, Douze's folder (the default cache, the caches of commands).
        var douzeFolder = site.SourceDirectory is { } sourceDirectory ? Path.Combine(sourceDirectory, DefaultCacheFolder) : null;
        ExcludedSourceFolders = [.. new[] { InsideSource(site, site.OutputDirectory), InsideSource(site, CacheRoot), InsideSource(site, douzeFolder) }.OfType<FPath>().Distinct().Order()];

        if (options.Cache is { } shared)
        {
            Cache = shared;
            Cache.BeginBuild();
        }
        else if (options.UseCache && CacheRoot != null)
        {
            Cache = BuildCache.Open(Path.Combine(CacheRoot, "cache"), view);
            OwnsCache = true;
        }

        BaseScopes = ScopeSet.FromSettings(SiteValues(site));

        _images = new Lazy<ImageProcessor>(() => options.Images(message => view.Message(LogLevel.Warning, null, $"libvips: {message}")), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<ImageProcessor> _images;

    /// <summary>
    /// The virtual path of a folder of the disk when it is inside the source folder.
    /// </summary>
    internal static FPath? InsideSource(SiteOptions site, string? directory)
    {
        if (site.SourceDirectory is not { } sourceDirectory || directory == null)
        {
            return null;
        }

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)) + Path.DirectorySeparatorChar;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;

        if (!root.StartsWith(sourceRoot, StringComparison.Ordinal) || root.Length == sourceRoot.Length)
        {
            return null;
        }

        return new FPath("/" + root[sourceRoot.Length..].Replace(Path.DirectorySeparatorChar, '/'));
    }

    /// <summary>
    /// What templates see of the site settings, under <c>site</c>: <c>url</c> when it is set, and <c>locales</c>
    /// (<see cref="LocaleSet.ToNode"/>) when the site has locales. The other settings (folders, output, parallelism)
    /// configure the build and are not exposed.
    /// </summary>
    private static NodeObject SiteValues(SiteOptions site)
    {
        var values = new NodeObject();

        if (site.Url != null)
        {
            values["url"] = NodeValue.Create(site.Url);
        }

        if (site.Locales.IsEnabled)
        {
            values["locales"] = site.Locales.ToNode();
        }

        return values.Count == 0 ? new NodeObject() : new NodeObject { ["site"] = values };
    }

    /// <summary>
    /// The folder of the cache and of the temporary files in the source folder: <c>.douze</c>.
    /// </summary>
    public const string DefaultCacheFolder = ".douze";

    public SiteOptions Site { get; }
    public BuildOptions Options { get; }
    public IFileSystem Source => Site.Source;
    public IFileSystem Output => Site.Output;
    public TemplateLoader Templates { get; }        // shared parsed template cache + include file provider
    public TemplateOptions LiquidOptions { get; }   // built once by LiquidOptions.Create
    public MarkdownRenderer Markdown { get; }       // the site's Markdown pipeline (the markdown section of douze.yml), built once
    public ShortcodeRegistry Shortcodes { get; }    // the shortcodes of site.shortcodes, registered on the parser of Templates

    /// <summary>
    /// The libvips bridge of the image features, created on first use, once per build: a build without image features
    /// never loads it.
    /// </summary>
    /// <exception cref="DouzeException">libvips is missing; every unit that needs it fails with the install hint.</exception>
    public ImageProcessor Images => _images.Value;

    public ITraceRecorder Trace { get; }
    public IProgressView View { get; }
    public int MaxDegreeOfParallelism { get; }
    public SemaphoreSlim Parallelism { get; }       // CPU work slots for built-in steps
    public SemaphoreSlim ProcessSlots { get; }      // slots for external processes (command steps)
    public CancellationToken Cancellation { get; }  // user cancellation (Ctrl+C)
    public string TempRoot { get; }                 // <cache dir>/tmp/<build id>/ for batch commands, created on demand, deleted by DisposeAsync
    public ScopeSet BaseScopes { get; }             // the site settings templates see (site.url, site.locales), below every contribution
    public LocaleSet Locales => Site.Locales;       // site.locales; LocaleSet.None when absent
    public ImmutableArray<FPath> ExcludedSourceFolders { get; }  // the output, cache and .douze folders, when they are inside the source folder: never sources
    public string? CacheRoot { get; }               // --cache-dir, or <source>/.douze/; null for a site not on disk
    public BuildCache? Cache { get; }               // the disk cache in <cache root>/cache/; null with --no-cache or when another build holds it
    public bool OwnsCache { get; }                  // opened by this build, which saves it; false for BuildOptions.Cache, which the build commits

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warned = new(StringComparer.Ordinal);

    /// <summary>
    /// Reports a warning of <paramref name="step"/> the first time the build sees <paramref name="message"/>, such as a
    /// missing translation that many pages use.
    /// </summary>
    public void WarnOnce(string step, string message)
    {
        if (_warned.TryAdd(message, true))
        {
            View.Message(LogLevel.Warning, step, message);
        }
    }

    /// <summary>
    /// Releases the slots and deletes <see cref="TempRoot"/>, unless <c>--verbose</c> asked to keep what the batch
    /// commands left in it (their units delete their own folders otherwise).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Parallelism.Dispose();
        ProcessSlots.Dispose();
        if (OwnsCache)
        {
            Cache?.Dispose();
        }

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
