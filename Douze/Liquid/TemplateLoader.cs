using System.Collections.Concurrent;
using System.Collections.Immutable;
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

    // Files of the includes folders by relative path, read once, with the paths looked for before them.
    private readonly ConcurrentDictionary<FPath, Lazy<Task<Resolution>>> _files = new();

    // Parsed includes by the cache key Fluid gives the template cache: the content hash of the file, which
    // GetFileInfoAsync returns as the key of its source.
    private readonly ConcurrentDictionary<string, IFluidTemplate> _includeTemplates = new(StringComparer.Ordinal);

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
    /// The number of parsed includes kept for <c>{% include %}</c> and <c>{% render %}</c>.
    /// </summary>
    public int IncludeCount => _includeTemplates.Count;

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
        private volatile WeakReference<ParseResult>? _result;

        public bool IsAlive => _result is not { } result || result.TryGetTarget(out _);

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
        var resolution = await GetFileAsync(relativePath.ToRelative()).WaitAsync(ct);

        resolution.Record(deps);

        return resolution.File?.Loaded.Value;
    }

    /// <summary>
    /// Forgets a source file that changed, was added or was deleted (watch mode): the names that resolved to it, the
    /// names that resolved to nothing (they may resolve to it now), and the parsed include of its content. A directory
    /// path forgets every file under it.
    /// </summary>
    public void Invalidate(FPath sourcePath)
    {
        foreach (var entry in _files)
        {
            var resolution = entry.Value.IsValueCreated && entry.Value.Value.IsCompletedSuccessfully ? entry.Value.Value.Result : null;
            var file = resolution?.File;

            // Entries that were not found, or found after the path, may resolve to the new file.
            if (file == null || file.Path == sourcePath || sourcePath.IsDirectory && file.Path.IsInDirectory(sourcePath)
                || resolution!.Missing.Any(x => x == sourcePath || sourcePath.IsDirectory && x.IsInDirectory(sourcePath)))
            {
                _files.TryRemove(entry.Key, out _);

                if (file != null)
                {
                    Forget(file.ContentHash);
                }
            }
        }
    }

    /// <summary>
    /// Forgets every file read from the includes folders (watch mode, when the changes are not known).
    /// </summary>
    public void InvalidateAll()
    {
        _files.Clear();
        _includeTemplates.Clear();
    }

    /// <summary>
    /// Drops the slots of parsed templates that nobody holds anymore: a watch session parses the new content of every
    /// page it rebuilds.
    /// </summary>
    public void Trim()
    {
        foreach (var (hash, slot) in _parsed)
        {
            if (!slot.IsAlive)
            {
                _parsed.TryRemove(new KeyValuePair<Hash128, ParseSlot>(hash, slot));
            }
        }
    }

    /// <summary>
    /// The hash of a file of the includes folders as this loader sees it, or <see cref="DependencySet.Absent"/> for a
    /// path it looked for and did not find. Returns <c>false</c> when the loader did not look for it: units that run
    /// would read it as it is on disk.
    /// </summary>
    public bool TryGetHash(FPath sourcePath, out Hash128 hash)
    {
        foreach (var entry in _files)
        {
            if (!entry.Value.IsValueCreated || !entry.Value.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            var resolution = entry.Value.Value.Result;

            if (resolution.File is { } file && file.Path == sourcePath)
            {
                hash = file.ContentHash;
                return true;
            }

            if (resolution.Missing.Contains(sourcePath))
            {
                hash = DependencySet.Absent;
                return true;
            }
        }

        hash = default;
        return false;
    }

    /// <summary>
    /// Forgets the parsed include of a content.
    /// </summary>
    private void Forget(Hash128 contentHash)
    {
        _includeTemplates.TryRemove(contentHash.ToString(), out _);
    }

    async ValueTask<TemplateSourceInfo> ITemplateFileProvider.GetFileInfoAsync(string subpath, TemplateContext context, CancellationToken cancellationToken)
    {
        var resolution = await ResolveIncludeAsync(subpath).WaitAsync(cancellationToken);

        if (context.AmbientValues.TryGetValue(AmbientKeys.Dependencies, out var deps) && deps is DependencyTracker tracker)
        {
            resolution.Record(tracker);
        }

        if (resolution.File is not { } file)
        {
            throw new DouzeException($"include '{subpath}' not found in {string.Join(", ", _includesFolders.Select(x => $"'{x}'"))}");
        }

        var bytes = file.Bytes;
        return new TemplateSourceInfo(DateTimeOffset.MinValue, _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)), file.ContentHash.ToString());
    }

    // Fluid calls the template cache with the key of the source GetFileInfoAsync returned: the content hash. The
    // modification date is ignored.
    bool ITemplateCache.TryGetTemplate(string key, DateTimeOffset lastModified, [MaybeNullWhen(false)] out IFluidTemplate template)
    {
        return _includeTemplates.TryGetValue(key, out template);
    }

    void ITemplateCache.SetTemplate(string key, DateTimeOffset lastModified, IFluidTemplate template)
    {
        _includeTemplates.TryAdd(key, template);
    }

    private async Task<Resolution> ResolveIncludeAsync(string subpath)
    {
        var path = new FPath(subpath).ToRelative();

        if (string.IsNullOrEmpty(path.GetExtensionWithDot()))
        {
            var liquid = await GetFileAsync(new FPath(path.FullName + ".liquid"));

            if (liquid.File != null)
            {
                return liquid;
            }

            var html = await GetFileAsync(new FPath(path.FullName + ".html"));
            return html with { Missing = liquid.Missing.AddRange(html.Missing) };
        }

        return await GetFileAsync(path);
    }

    private Task<Resolution> GetFileAsync(FPath relativePath)
    {
        return _files.GetOrAdd(relativePath, static (path, loader) => new Lazy<Task<Resolution>>(
            () => loader.ReadFileAsync(path), LazyThreadSafetyMode.ExecutionAndPublication), this).Value;
    }

    private async Task<Resolution> ReadFileAsync(FPath relativePath)
    {
        var missing = ImmutableArray.CreateBuilder<FPath>();

        foreach (var folder in _includesFolders)
        {
            var path = FPath.Combine(folder, relativePath);

            if (!(await _source.GetFileEntryAsync(path)).Exists)
            {
                missing.Add(path);
                continue;
            }

            byte[] bytes;

            await using (var stream = await _source.OpenFileAsync(path))
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                bytes = memory.ToArray();
            }

            return new Resolution(new IncludeFile(this, path, bytes, Hash128.Of(bytes)), missing.ToImmutable());
        }

        return new Resolution(null, missing.ToImmutable());
    }

    /// <summary>
    /// The file a name resolved to, if any, and the paths looked for before it: a file added at one of them later would
    /// be used instead, so a unit depends on their absence too.
    /// </summary>
    private sealed record Resolution(IncludeFile? File, ImmutableArray<FPath> Missing)
    {
        public void Record(DependencyTracker deps)
        {
            foreach (var path in Missing)
            {
                deps.RecordTemplate(path, DependencySet.Absent);
            }

            if (File != null)
            {
                deps.RecordTemplate(File.Path, File.ContentHash);
            }
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

            var (values, body) = FrontMatter.Parse(text);

            return new LoadedTemplate(Path, loader.GetOrParse(Hash128.OfText(body), body, Path.FullName), values, ContentHash);
        }
    }
}
