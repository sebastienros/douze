using System.Text;
using Douze.DotNetApi;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: dotnet-api</c>. Reads .NET assemblies (<c>.dll</c>, their metadata, with <see cref="AssemblyReader"/>) and
/// their XML documentation files (the <c>.xml</c> of the same name; other <c>.xml</c> files document what the
/// assemblies inherit), and contributes their API as a site value (<c>with.as</c>, default <c>api</c>): assemblies,
/// namespaces, types and members, each with its url under <c>with.url</c> (default <c>/api/</c>), its signature and its
/// documentation as HTML. It emits no files: pages paginate over <c>api.namespaces</c>, <c>api.types</c> or
/// <c>api.members</c>, so that the site's templates decide what a page shows. Warnings are kept in a memo and logged
/// again when the step is replayed.
/// </summary>
internal sealed class DotNetApiStep : Step, IReduceStep, IReplayObserver
{
    private static readonly GlobSet Files = GlobSet.Parse(["**/*.{dll,xml}"]);

    private readonly string _as;
    private readonly string _url;
    private readonly bool _memberPages;
    private readonly bool _protected;
    private readonly IReadOnlyList<ApiNodeBuilder.ExternalDocs> _external;
    private BuildContext? _build;

    public DotNetApiStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "as", "url", "member-pages", "protected", "external");
        _as = options.GetString("as", "api")!;

        if (_as.Length == 0)
        {
            throw new WorkflowValidationException($"option 'as' of step '{Name}' must not be empty", definition.Location);
        }

        var url = options.GetString("url", "/api/")!.Trim();
        _url = "/" + url.Trim('/') + (url.Trim('/').Length == 0 ? "" : "/");
        _memberPages = options.GetBool("member-pages");
        _protected = options.GetBool("protected", defaultValue: true);

        if (definition.With["external"] is { } external)
        {
            if (external is not NodeObject map || map.Any(x => x.Value is not NodeValue<string>))
            {
                throw new WorkflowValidationException($"option 'external' of step '{Name}' must be a mapping of namespaces to url patterns", definition.Location);
            }

            // Longer prefixes first: Microsoft.Extensions before Microsoft.
            _external = [.. map.Select(x => new ApiNodeBuilder.ExternalDocs(x.Key, ((NodeValue<string>)x.Value!).Value)).OrderByDescending(x => x.Prefix.Length).ThenBy(x => x.Prefix, StringComparer.Ordinal)];
        }
        else
        {
            _external = ApiNodeBuilder.DefaultExternal;
        }
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => Files;

    // The API depends on the assemblies and their documentation only.
    public override bool ReadsScopes => false;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;
        return default;
    }

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var dlls = files.Where(x => IsExtension(x, ".dll")).ToList();
        var xmls = files.Where(x => IsExtension(x, ".xml")).ToList();
        var assemblies = new ApiAssembly[dlls.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Build.MaxDegreeOfParallelism, CancellationToken = ct };

        await Parallel.ForEachAsync(Enumerable.Range(0, dlls.Count), options, async (i, ct) =>
        {
            var bytes = await dlls[i].Content.ReadBytesAsync(ct);
            assemblies[i] = AssemblyReader.Read(bytes.ToArray(), dlls[i].Path.FullName, _protected);
        });

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            if (!names.TryAdd(assembly.Name, assembly.Path))
            {
                throw new DouzeException($"the assembly '{assembly.Name}' is read from both '{names[assembly.Name]}' and '{assembly.Path}': give the step one of them (a target framework)");
            }
        }

        // The documentation of the assemblies first, so that it wins over the files that document what they inherit.
        var docs = new DocSet();
        var paired = new HashSet<VirtualFile>();
        var warnings = new List<string>();

        foreach (var dll in dlls)
        {
            var xmlPath = dll.Path.FullName[..^4] + ".xml";

            if (xmls.FirstOrDefault(x => string.Equals(x.Path.FullName, xmlPath, StringComparison.OrdinalIgnoreCase)) is { } xml)
            {
                await AddAsync(docs, xml, ct);
                paired.Add(xml);
            }
            else
            {
                warnings.Add($"'{dll.Path}' has no XML documentation file next to it ('{xmlPath}'): its API is listed without documentation");
            }
        }

        foreach (var xml in xmls)
        {
            if (!paired.Contains(xml))
            {
                await AddAsync(docs, xml, ct);
            }
        }

        var builder = new ApiNodeBuilder(_url, _memberPages, _external, docs);
        var api = builder.Build([.. assemblies.OrderBy(x => x.Name, StringComparer.Ordinal)]);

        warnings.AddRange(builder.Warnings);

        foreach (var warning in warnings)
        {
            context.Log(LogLevel.Warning, warning);
        }

        if (warnings.Count > 0)
        {
            context.Build.Cache?.PutMemo(Name, WarningsKey(files), Encoding.UTF8.GetBytes(string.Join('\n', warnings)));
        }

        return new StepOutput(FileList.None, new SiteScope($"step:{Name}", new NodeObject { [_as] = api }));
    }

    /// <summary>
    /// Logs the warnings of a replayed step again: an assembly without documentation is still without it.
    /// </summary>
    public void Replayed(IReadOnlyList<VirtualFile> inputs, IReadOnlyList<VirtualFile> outputs)
    {
        if (_build?.Cache is { } cache && cache.TryGetMemo(Name, WarningsKey(inputs), out var memo))
        {
            foreach (var warning in Encoding.UTF8.GetString(memo).Split('\n'))
            {
                _build.View.Message(LogLevel.Warning, Name, warning);
            }
        }
    }

    private Hash128 WarningsKey(IReadOnlyList<VirtualFile> files)
    {
        var hashes = new Hash128[files.Count * 2 + 1];
        hashes[0] = Definition.ConfigHash;

        for (var i = 0; i < files.Count; i++)
        {
            hashes[i * 2 + 1] = Hash128.OfText(files[i].Path.FullName);
            hashes[i * 2 + 2] = files[i].ContentHash;
        }

        return Hash128.Combine(hashes);
    }

    private static async ValueTask AddAsync(DocSet docs, VirtualFile xml, CancellationToken ct)
    {
        await using var stream = await xml.Content.OpenReadAsync(ct);
        docs.Add(stream, xml.Path.FullName);
    }

    private static bool IsExtension(VirtualFile file, string extension)
    {
        return string.Equals(file.Path.GetExtensionWithDot(), extension, StringComparison.OrdinalIgnoreCase);
    }
}
