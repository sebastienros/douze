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

    // Parsed templates by content hash: identical templates (paginated pages, layouts) are parsed once while they are in
    // use. A failed parse is kept too, so the error is reported for every file with that content, each with its own
    // path. The templates are held weakly: the body of every page is a template, and keeping all of them would keep
    // every page in memory until the build ends (layouts and includes are held by their files).
    private readonly ConcurrentDictionary<Hash128, ParseSlot> _parsed = new();

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
    /// The number of distinct templates parsed (or that failed to parse).
    /// </summary>
    public int ParsedCount => _parsed.Count;

    /// <summary>
    /// Returns the parsed template for <paramref name="text"/>, parsing it only once per <paramref name="contentHash"/>.
    /// </summary>
    /// <exception cref="TemplateParseException">The template is invalid; the exception names <paramref name="displayPath"/>.</exception>
    public ParsedTemplate GetOrParse(Hash128 contentHash, string text, string displayPath)
    {
        var result = _parsed.GetOrAdd(contentHash, static _ => new ParseSlot()).GetOrParse(_parser, contentHash, text);

        if (result.Template == null)
        {
            var error = result.Error!;
            throw new TemplateParseException(displayPath, error.Position?.Line, error.Position?.Column, Reason(error));
        }

        return result.Template;
    }

    private sealed record ParseResult(ParsedTemplate? Template, ParseException? Error);

    /// <summary>
    /// The parse of one content: parsed once while somebody holds the result, again after it was collected.
    /// </summary>
    private sealed class ParseSlot
    {
        private readonly Lock _lock = new();
        private WeakReference<ParseResult>? _result;

        public ParseResult GetOrParse(FluidParser parser, Hash128 hash, string text)
        {
            if (_result != null && _result.TryGetTarget(out var result))
            {
                return result;
            }

            lock (_lock)
            {
                if (_result != null && _result.TryGetTarget(out result))
                {
                    return result;
                }

                try
                {
                    result = new ParseResult(new ParsedTemplate(parser.Parse(text), hash), null);
                }
                catch (ParseException e)
                {
                    result = new ParseResult(null, e);
                }

                _result = new WeakReference<ParseResult>(result);
                return result;
            }
        }
    }

    /// <summary>
    /// The first line of a Fluid parse error, without the position it repeats ("Invalid 'if' tag at (2:8)").
    /// </summary>
    private static string Reason(ParseException error)
    {
        var message = error.Message;
        var lineEnd = message.IndexOf('\n');
        message = (lineEnd >= 0 ? message[..lineEnd] : message).TrimEnd();

        if (error.Position is { } position)
        {
            var suffix = $" at ({position.Line}:{position.Column})";

            if (message.EndsWith(suffix, StringComparison.Ordinal))
            {
                message = message[..^suffix.Length];
            }
        }

        return message;
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

            var (values, body) = FrontMatter.Parse(text);

            return new LoadedTemplate(Path, loader.GetOrParse(Hash128.OfText(body), body, Path.FullName), values, ContentHash);
        }
    }
}
