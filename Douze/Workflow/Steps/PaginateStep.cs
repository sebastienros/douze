using System.Globalization;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: paginate</c>. Expands a file whose <c>pagination</c> names a list (<c>data</c>, <c>size</c>, <c>alias</c>,
/// <c>resolve: values</c>) into one file per page. Each page's <c>permalink</c> is resolved with its own
/// <c>pagination</c> and alias, and stored as a concrete path; when every page resolves to the same path (no
/// <c>permalink</c>, or one that does not depend on the page), pages get paged paths: <c>/a/index.html</c>,
/// <c>/a/1/index.html</c>... Files without <c>pagination</c> pass through.
/// </summary>
/// <remarks>
/// A page's <c>pagination</c> is <c>items</c>, <c>pageNumber</c>, <c>hrefs</c> (every page's path), <c>pages</c> and
/// <c>page.next</c>, <c>page.previous</c>, <c>page.first</c>, <c>page.last</c>. The pages are summaries
/// (<c>items</c>, <c>pageNumber</c>, <c>href</c>) shared by every page: linking the full pagination objects would form
/// cycles, which metadata hashing cannot follow.
/// </remarks>
internal sealed class PaginateStep : Step, IMapStep
{
    private static readonly GlobSet HtmlFiles = GlobSet.Parse(["**/*.html"]);

    private readonly FPath? _root;

    public PaginateStep(StepDefinition definition) : base(definition)
    {
        var options = new WithOptions(definition, "root");
        _root = options.GetString("root") is { } root ? StepPaths.Directory(root) : (FPath?)null;
    }

    public override StepShape NativeShape => StepShape.Map;

    public override GlobSet DefaultMatch => HtmlFiles;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var scopes = context.Scopes;
        var deps = context.Dependencies;

        if (!scopes.TryResolve(file.Metadata, "pagination", null, deps, out var node, out _)
            || node is not NodeObject pagination
            || pagination["data"] is not NodeValue<string> { Value: { Length: > 0 } dataName }
            || pagination["size"] is not NodeValue sizeNode)
        {
            return FileList.Of(file);
        }

        var data = ResolveData(file.Metadata, dataName, scopes, deps);

        if (data == null)
        {
            return FileList.Of(file);
        }

        var size = GetSize(sizeNode);
        var resolveValues = pagination["resolve"] is NodeValue<string> { Value: "values" };
        var alias = pagination["alias"] is NodeValue<string> { Value: { Length: > 0 } a } ? a : null;

        IReadOnlyList<Node?> items = data switch
        {
            // The items of an object are its keys, or its values with 'resolve: values'.
            NodeObject obj => obj.Select(x => resolveValues ? x.Value : NodeValue.Create(x.Key)).ToArray(),
            NodeArray array => array.ToArray(),
            _ => throw new DouzeException($"'pagination.data' of '{file.SourcePath ?? file.Path}' must name a list or an object, '{dataName}' is neither"),
        };

        var pageCount = (items.Count + size - 1) / size;
        var summaries = new NodeObject[pageCount];
        var paginations = new NodeObject[pageCount];
        var pagesArray = new NodeArray();
        var hrefs = new NodeArray();

        for (var i = 0; i < pageCount; i++)
        {
            var pageItems = new NodeArray(items.Skip(size * i).Take(size).ToArray());
            summaries[i] = new NodeObject { ["items"] = pageItems, ["pageNumber"] = NodeValue.Create(i) };
            pagesArray.Add(summaries[i]);
        }

        var metadatas = new Metadata[pageCount];
        var paths = new FPath?[pageCount];
        var root = _root ?? context.Build.Site.ContentFolder;

        for (var i = 0; i < pageCount; i++)
        {
            var pageItems = (NodeArray)summaries[i]["items"]!;

            paginations[i] = new NodeObject
            {
                ["items"] = pageItems,
                ["pageNumber"] = NodeValue.Create(i),
                ["pages"] = pagesArray,
                ["hrefs"] = hrefs,
                ["page"] = new NodeObject
                {
                    ["next"] = i < pageCount - 1 ? summaries[i + 1] : null,
                    ["previous"] = i > 0 ? summaries[i - 1] : null,
                    ["first"] = summaries[0],
                    ["last"] = summaries[pageCount - 1],
                },
            };

            var page = paginations[i];

            metadatas[i] = file.Metadata.With(own =>
            {
                own["pagination"] = page;

                if (alias != null)
                {
                    own[alias] = size == 1 ? pageItems[0] : pageItems;
                }
            });

            paths[i] = await PermalinkResolver.ResolveAsync(file, metadatas[i], context, root, ct);
        }

        // Pages that all resolve to one path (no permalink, or one that ignores the page) get paged paths.
        if (pageCount > 1 && paths.All(x => x == paths[0]) && paths[0] is { } shared)
        {
            for (var i = 0; i < pageCount; i++)
            {
                paths[i] = PermalinkResolver.Paged(shared, i);
            }
        }

        // The pagination objects are not published yet: hrefs can still be filled in.
        for (var i = 0; i < pageCount; i++)
        {
            var href = paths[i] is { } p ? NodeValue.Create(p.FullName) : null;
            hrefs.Add(href);
            summaries[i]["href"] = href;
        }

        var outputs = new List<VirtualFile>(pageCount);

        for (var i = 0; i < pageCount; i++)
        {
            // permalink: false drops the page.
            if (paths[i] is not { } path)
            {
                continue;
            }

            var metadata = metadatas[i].With(own => own[PermalinkResolver.Key] = NodeValue.Create(path.FullName));
            outputs.Add(file.With(Name, path: path, metadata: metadata));
        }

        return FileList.Of(outputs);
    }

    /// <summary>
    /// Resolves <c>pagination.data</c>: the first segment through the scopes, the others as nested keys
    /// (<c>collections.post</c>).
    /// </summary>
    private static Node? ResolveData(Metadata metadata, string name, ScopeSet scopes, DependencyTracker deps)
    {
        var segments = name.Split('.');

        if (!scopes.TryResolve(metadata, segments[0], null, deps, out var data, out _))
        {
            return null;
        }

        for (var i = 1; i < segments.Length && data != null; i++)
        {
            data = data is NodeObject obj ? obj[segments[i]] : null;
        }

        return data;
    }

    private static int GetSize(NodeValue node)
    {
        var size = node switch
        {
            NodeValue<int> i => i.Value,
            NodeValue<long> l => (int)l.Value,
            NodeValue<decimal> m => (int)m.Value,
            NodeValue<string> s when int.TryParse(s.Value, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        if (size < 1)
        {
            throw new DouzeException($"'pagination.size' must be a positive integer");
        }

        return size;
    }
}
