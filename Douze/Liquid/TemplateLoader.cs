using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Fluid;

namespace Douze.Liquid;

internal sealed record ParsedTemplate(IFluidTemplate Template, Hash128 Hash);

internal sealed record LoadedTemplate(FPath Path, ParsedTemplate Template, NodeObject FrontMatter, Hash128 ContentHash);

/// <summary>
/// Parses templates once per distinct content, and loads layouts, includes and templates from the includes folders.
/// Also the file provider and template cache of Fluid's <c>{% include %}</c> and <c>{% render %}</c>.
/// </summary>
internal sealed class TemplateLoader : ITemplateFileProvider, ITemplateCache
{
    private readonly IFileSystem _source;
    private readonly IReadOnlyList<FPath> _includesFolders;
    private readonly FluidParser _parser;

    // Parsed templates by content hash: identical templates (paginated pages, layouts) are parsed once.
    private readonly ConcurrentDictionary<Hash128, Lazy<ParsedTemplate>> _parsed = new();

    // Files of the includes folders by relative path, read once.
    private readonly ConcurrentDictionary<FPath, Lazy<Task<IncludeFile?>>> _files = new();

    // Fluid include names to the content hash of the file they resolved to, for the template cache.
    private readonly ConcurrentDictionary<string, Hash128> _includeHashes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Hash128, IFluidTemplate> _includeTemplates = new();

    public TemplateLoader(IFileSystem source, IReadOnlyList<FPath> includesFolders, FluidParser parser)
    {
        _source = source;
        _includesFolders = includesFolders;
        _parser = parser;
    }

    public FluidParser Parser => _parser;

    /// <summary>
    /// Returns the parsed template for <paramref name="text"/>, parsing it only once per <paramref name="contentHash"/>.
    /// </summary>
    /// <exception cref="DouzeException"><c>&lt;displayPath&gt;(&lt;line&gt;,&lt;col&gt;): &lt;message&gt;</c> when the template is invalid.</exception>
    public ParsedTemplate GetOrParse(Hash128 contentHash, string text, string displayPath)
    {
        var lazy = _parsed.GetOrAdd(contentHash, static (hash, state) => new Lazy<ParsedTemplate>(
            () => new ParsedTemplate(state.Loader.Parse(state.Text, state.DisplayPath), hash),
            LazyThreadSafetyMode.ExecutionAndPublication), (Loader: this, Text: text, DisplayPath: displayPath));

        return lazy.Value;
    }

    /// <summary>
    /// Loads a template from the first includes folder that contains <paramref name="relativePath"/>, strips its front
    /// matter, and records it in <paramref name="deps"/>. Returns <c>null</c> when no folder contains it.
    /// </summary>
    public async ValueTask<LoadedTemplate?> LoadAsync(FPath relativePath, DependencyTracker deps, CancellationToken ct)
    {
        var file = await GetFileAsync(relativePath.ToRelative()).WaitAsync(ct);

        if (file == null)
        {
            return null;
        }

        deps.RecordTemplate(file.Path, file.ContentHash);

        return file.Loaded.Value;
    }

    /// <summary>
    /// Forgets a source file that changed (watch mode).
    /// </summary>
    public void Invalidate(FPath sourcePath)
    {
        foreach (var entry in _files)
        {
            var file = entry.Value.IsValueCreated && entry.Value.Value.IsCompletedSuccessfully ? entry.Value.Value.Result : null;

            // Entries that were not found may resolve to the new file.
            if (file == null || file.Path == sourcePath)
            {
                _files.TryRemove(entry.Key, out _);
            }
        }
    }

    async ValueTask<TemplateSourceInfo> ITemplateFileProvider.GetFileInfoAsync(string subpath, TemplateContext context, CancellationToken cancellationToken)
    {
        var file = await ResolveIncludeAsync(subpath).WaitAsync(cancellationToken);

        if (file == null)
        {
            throw new DouzeException($"include '{subpath}' not found in {string.Join(", ", _includesFolders.Select(x => $"'{x}'"))}");
        }

        if (context.AmbientValues.TryGetValue(AmbientKeys.Dependencies, out var deps) && deps is DependencyTracker tracker)
        {
            tracker.RecordTemplate(file.Path, file.ContentHash);
        }

        _includeHashes[subpath] = file.ContentHash;

        var bytes = file.Bytes;
        return new TemplateSourceInfo(DateTimeOffset.MinValue, _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)), file.ContentHash.ToString());
    }

    bool ITemplateCache.TryGetTemplate(string subpath, DateTimeOffset lastModified, [MaybeNullWhen(false)] out IFluidTemplate template)
    {
        // The modification date is ignored: the cache is keyed by content hash.
        if (_includeHashes.TryGetValue(subpath, out var hash) && _includeTemplates.TryGetValue(hash, out var cached))
        {
            template = cached;
            return true;
        }

        template = null;
        return false;
    }

    void ITemplateCache.SetTemplate(string subpath, DateTimeOffset lastModified, IFluidTemplate template)
    {
        if (_includeHashes.TryGetValue(subpath, out var hash))
        {
            _includeTemplates.TryAdd(hash, template);
        }
    }

    private async Task<IncludeFile?> ResolveIncludeAsync(string subpath)
    {
        var path = new FPath(subpath).ToRelative();

        if (string.IsNullOrEmpty(path.GetExtensionWithDot()))
        {
            return await GetFileAsync(new FPath(path.FullName + ".liquid")) ?? await GetFileAsync(new FPath(path.FullName + ".html"));
        }

        return await GetFileAsync(path);
    }

    private Task<IncludeFile?> GetFileAsync(FPath relativePath)
    {
        return _files.GetOrAdd(relativePath, static (path, loader) => new Lazy<Task<IncludeFile?>>(
            () => loader.ReadFileAsync(path), LazyThreadSafetyMode.ExecutionAndPublication), this).Value;
    }

    private async Task<IncludeFile?> ReadFileAsync(FPath relativePath)
    {
        foreach (var folder in _includesFolders)
        {
            var path = FPath.Combine(folder, relativePath);

            if (!(await _source.GetFileEntryAsync(path)).Exists)
            {
                continue;
            }

            byte[] bytes;

            await using (var stream = await _source.OpenFileAsync(path))
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                bytes = memory.ToArray();
            }

            return new IncludeFile(this, path, bytes, Hash128.Of(bytes));
        }

        return null;
    }

    private IFluidTemplate Parse(string text, string displayPath)
    {
        try
        {
            return _parser.Parse(text);
        }
        catch (ParseException e)
        {
            var position = e.Position is { } p ? $"({p.Line},{p.Column})" : "";
            throw new DouzeException($"{displayPath}{position}: {e.Message}");
        }
    }

    private sealed class IncludeFile
    {
        public IncludeFile(TemplateLoader loader, FPath path, byte[] bytes, Hash128 contentHash)
        {
            Path = path;
            Bytes = bytes;
            ContentHash = contentHash;
            Loaded = new Lazy<LoadedTemplate>(() => Load(loader), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public FPath Path { get; }
        public byte[] Bytes { get; }
        public Hash128 ContentHash { get; }
        public Lazy<LoadedTemplate> Loaded { get; }

        private LoadedTemplate Load(TemplateLoader loader)
        {
            var text = FileContent.FromBytes(Bytes, isText: true).ReadTextAsync(CancellationToken.None).GetAwaiter().GetResult();

            // TODO(phase 1b): FrontMatter.Parse replaces FrontMatterHelper, whose Options argument is unused.
            var (frontMatter, body) = FrontMatterHelper.ParseFrontMatterAsync(new StringReader(text), null!).GetAwaiter().GetResult();
            var values = string.IsNullOrWhiteSpace(frontMatter) ? new NodeObject() : YamlHelper.Parse(frontMatter) as NodeObject ?? new NodeObject();

            return new LoadedTemplate(Path, loader.GetOrParse(Hash128.OfText(body), body, Path.FullName), values, ContentHash);
        }
    }
}
