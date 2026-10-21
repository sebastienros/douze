using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: data</c>. Parses data files (YAML, JSON, XML) and contributes them as scopes. With <c>scope: site</c>, each
/// file becomes a site key named after its path relative to <c>root</c>: <c>_data/pkgs/a.json</c> is <c>pkgs.a</c>.
/// With <c>scope: folder</c>, the top-level keys of each file apply to the files of its directory and below.
/// </summary>
internal sealed class DataStep : Step, IReduceStep
{
    private static readonly GlobSet DataFiles = GlobSet.Parse(["**/*.{yml,yaml,json,xml}"]);

    private readonly bool _site;
    private readonly FPath? _root;

    public DataStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "scope", "root");

        _site = options.GetRequiredString("scope") switch
        {
            "site" => true,
            "folder" => false,
            var other => throw new WorkflowValidationException($"option 'scope' of step '{Name}' must be 'site' or 'folder', not '{other}'", definition.Location),
        };

        _root = options.GetString("root") is { } root ? StepPaths.Directory(root) : (FPath?)null;
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override GlobSet DefaultMatch => DataFiles;

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        var root = _root ?? (_site ? context.Build.Site.DataFolder : context.Build.Site.ContentFolder);

        foreach (var file in files)
        {
            if (!file.Path.IsInDirectory(root))
            {
                throw new DouzeException($"data file '{file.Path}' is not under the root '{root}'");
            }
        }

        // Parsed in parallel, assembled in path order.
        var values = new Node?[files.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Build.MaxDegreeOfParallelism, CancellationToken = ct };

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), options, async (i, ct) =>
        {
            values[i] = await ParseStep.ParseAsync(files[i], ct);
        });

        return _site ? Site(files, values, root) : Folders(files, values);
    }

    private StepOutput Site(IReadOnlyList<VirtualFile> files, Node?[] values, FPath root)
    {
        var site = new NodeObject();
        var keySources = new Dictionary<string, string>(StringComparer.Ordinal);

        // Which file or folder defined each key, for the duplicate error.
        var definedBy = new Dictionary<string, FPath>(StringComparer.Ordinal);

        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i].Path;
            var relative = path.FullName[root.FullName.Length..];
            var segments = relative.Split('/');
            segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]);

            var target = site;
            var key = "";

            for (var s = 0; s < segments.Length; s++)
            {
                key = s == 0 ? segments[0] : key + "." + segments[s];

                // A key is defined once: by a file, or by a folder whose files define its sub keys.
                if (definedBy.TryGetValue(key, out var existing) && (s == segments.Length - 1 || existing.IsFile))
                {
                    throw new DouzeException($"data key '{key}' is defined by both '{existing}' and '{path}'");
                }

                if (s == segments.Length - 1)
                {
                    target[segments[s]] = values[i];
                    definedBy[key] = path;
                }
                else
                {
                    if (target[segments[s]] is not NodeObject folder)
                    {
                        target[segments[s]] = folder = new NodeObject();
                        definedBy[key] = FPath.Combine(root, string.Join('/', segments[..(s + 1)]) + "/");
                    }

                    target = folder;
                }
            }

            // A key built from a folder of files is recorded as the folder: a replay checks every file under it.
            keySources[segments[0]] = segments.Length == 1 ? path.FullName : FPath.Combine(root, segments[0] + "/").FullName;
        }

        return new StepOutput(FileList.None, new SiteScope($"step:{Name}", site, keySources));
    }

    private static StepOutput Folders(IReadOnlyList<VirtualFile> files, Node?[] values)
    {
        var folders = new SortedDictionary<FPath, (NodeObject Values, List<FPath> Sources)>();

        // Files are sorted by path: a later file of the same folder overrides the keys of an earlier one.
        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i].Path;

            if (values[i] is null)
            {
                continue;
            }

            if (values[i] is not NodeObject obj)
            {
                throw new DouzeException($"folder data file '{path}' must contain a mapping");
            }

            var directory = path.GetDirectory();

            if (!folders.TryGetValue(directory, out var folder))
            {
                folders[directory] = folder = (new NodeObject(), []);
            }

            foreach (var property in obj)
            {
                folder.Values[property.Key] = property.Value;
            }

            folder.Sources.Add(path);
        }

        var scopes = folders.Select(x => new FolderScope(x.Key, x.Value.Values, [.. x.Value.Sources])).ToArray();

        return new StepOutput(FileList.None, Folders: scopes);
    }
}
