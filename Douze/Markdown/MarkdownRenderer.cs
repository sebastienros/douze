using System.Collections.Concurrent;
using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Workflow;
using Markdig;
using Markdig.Renderers;

namespace Douze.Markdown;

/// <summary>
/// Renders Markdown to HTML with the pipeline of <see cref="MarkdownOptions"/>. Built once per build
/// (<see cref="BuildContext.Markdown"/>) and used concurrently: Markdig's HTML renderer is not thread safe, so renders
/// take one from a pool. The result is final HTML: raw regions (<see cref="RawRegionExtension"/>) are written as they
/// are, and what is left of regions that did not start a line (U+001E markers, U+001D encoded newlines) is removed
/// from the output.
/// </summary>
internal sealed class MarkdownRenderer
{
    // A renderer whose buffer grew past this many characters is not pooled, so one large page does not keep its buffer.
    private const int MaxPooledCapacity = 1 << 20;

    private readonly IFileSystem _source;
    private readonly FPath _contentFolder;
    private readonly LinkRewriter? _links;
    private readonly ConcurrentBag<PooledHtmlRenderer> _pool = [];

    /// <exception cref="ArgumentException">An extension name is invalid (the loader validates them first).</exception>
    public MarkdownRenderer(MarkdownOptions options, IFileSystem source, FPath contentFolder)
    {
        Options = options;
        _source = source;
        _contentFolder = contentFolder;
        Pipeline = MarkdownPipelineFactory.Create(options);
        _links = options.RewriteLinks ? new LinkRewriter(source, contentFolder) : null;
    }

    public MarkdownOptions Options { get; }

    public MarkdownPipeline Pipeline { get; }

    /// <summary>
    /// A renderer with the same options but other extensions (the <c>extensions</c> option of a <c>markdown</c> step).
    /// </summary>
    public MarkdownRenderer WithExtensions(ImmutableArray<string> extensions)
    {
        return new MarkdownRenderer(Options with { Extensions = extensions }, _source, _contentFolder);
    }

    /// <param name="markdown">The Markdown source.</param>
    /// <param name="directory">The source folder of the page, which relative <c>.md</c> links start from; <c>null</c> leaves links as they are.</param>
    /// <param name="deps">Records the link targets that do not exist.</param>
    /// <param name="asideTitles">Titles of aside kinds that replace the configured ones (localized titles).</param>
    /// <param name="warn">Receives the warnings of the render: unknown aside kinds.</param>
    public async ValueTask<string> RenderAsync(string markdown, FPath? directory, DependencyTracker? deps, IReadOnlyDictionary<string, string>? asideTitles = null, Action<MarkdownWarning>? warn = null)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);

        if (_links != null && directory is { } folder)
        {
            await _links.RewriteAsync(document, folder, deps);
        }

        if (!_pool.TryTake(out var renderer))
        {
            renderer = new PooledHtmlRenderer();
            Pipeline.Setup(renderer);
        }

        renderer.State = asideTitles == null && warn == null ? null : new MarkdownRenderState(asideTitles, warn);
        renderer.Render(document);

        var html = renderer.TakeOutput(out var capacity);

        if (capacity <= MaxPooledCapacity)
        {
            _pool.Add(renderer);
        }

        return RawRegionExtension.StripMarkers(html);
    }
}

/// <summary>
/// A warning of a render, at a position of the Markdown source (1-based).
/// </summary>
internal readonly record struct MarkdownWarning(int Line, int Column, string Message);

/// <summary>
/// What the Douze renderers read during one render.
/// </summary>
internal sealed record MarkdownRenderState(IReadOnlyDictionary<string, string>? AsideTitles, Action<MarkdownWarning>? Warn);

/// <summary>
/// An HTML renderer over a reusable buffer, with the state of the current render.
/// </summary>
internal sealed class PooledHtmlRenderer : HtmlRenderer
{
    private readonly StringWriter _writer;

    public PooledHtmlRenderer() : this(new StringWriter())
    {
    }

    private PooledHtmlRenderer(StringWriter writer) : base(writer)
    {
        _writer = writer;
    }

    public MarkdownRenderState? State { get; set; }

    /// <summary>
    /// Returns what was rendered and resets the renderer for the next render.
    /// </summary>
    public string TakeOutput(out int capacity)
    {
        _writer.Flush();
        var builder = _writer.GetStringBuilder();
        var html = builder.ToString();
        capacity = builder.Capacity;
        State = null;
        Reset();
        return html;
    }
}
