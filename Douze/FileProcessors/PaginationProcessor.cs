using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Applies the 'pagination' properties.
/// </summary>
internal class PaginationProcessor : IFileProcessor
{
    internal static readonly string Pagination = "pagination";
    internal static readonly string Data = "data";
    internal static readonly string Size = "size";
    internal static readonly string Items = "items";

    public Predicate<FPath> Match { get; set; } = _ => true;

    public PaginationProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();
        var pages = new ConcurrentBag<NodeObject>();

        Parallel.ForEach(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, (document, cancellationToken) =>
        {
            if (!Match(document.Path))
            {
                return;
            }

            // No pagination property?
            if (!document.Data.TryGetItem(Pagination, out var paginationNode) || paginationNode == null || paginationNode is not NodeObject)
            {
                result.Add(new DocumentResult(document));
                return;
            }

            var paginationDataName = paginationNode[Data]?.GetValue<string>() ?? "";

            // No existing pagination.data property?
            if (!document.Data.TryGetItem(paginationDataName, out var paginationData) || paginationData == null)
            {
                result.Add(new DocumentResult(document));
                return;
            }

            // No existing pagination.size property?
            if (!paginationNode.AsObject().TryGetItem(Size, out var paginationSize) || paginationSize == null)
            {
                result.Add(new DocumentResult(document));
                return;
            }

            var resolveValue = paginationNode.AsObject().TryGetItem("resolve", out var resolveNode) && resolveNode != null && resolveNode.GetValue<string>() == "values";

            var aliasName = paginationNode.AsObject().TryGetItem("alias", out var aliasNode) && aliasNode != null ? aliasNode.GetValue<string>() : null;

            var size = Convert.ToInt32(paginationSize.GetValue<object>());

            // For each chunk of data a pagination object is created with these properties, and a new document created

            if (paginationData is NodeObject nodeObject)
            {
                // When pagination.data is a NodeObject, the items are the keys

                var numberOfPages = (int)Math.Ceiling((double)nodeObject.Count / size);

                for (var pageNumber = 0; pageNumber < numberOfPages; pageNumber++)
                {
                    var dataset = nodeObject.Skip(size * pageNumber).Take(size).ToArray();
                    var items = new NodeArray(dataset.Select(x => resolveValue ? x.Value : x.Key).ToArray());
                    var pagination = new NodeObject
                    {
                        [Items] = items,
                        ["pageNumber"] = pageNumber,
                    };

                    pages.Add(pagination);

                    var path = GeneratePagedPath(document.Path, pageNumber);

                    // TODO: the data of the new document is cloned which will also clone all global data.
                    // This ideally should only clone the document's own data instead

                    var newDocument = new CopyDocument(document, options.OutputFileSystem, path);
                    newDocument.Data = newDocument.Data.Clone().AsObject();
                    newDocument.Data["pagination"] = pagination;

                    if (aliasName != null)
                    {
                        newDocument.Data[aliasName] = size == 1 ? items[0] : items;
                    }

                    result.Add(new DocumentResult($"Generating file {path}", newDocument));
                }
            }
            else if (paginationData is NodeArray nodeArray)
            {
                var numberOfPages = (int)Math.Ceiling((double)nodeArray.Count / size);

                for (var pageNumber = 0; pageNumber < numberOfPages; pageNumber++)
                {
                    var items = new NodeArray(nodeArray.Skip(size * pageNumber).Take(size).ToArray());
                    var pagination = new NodeObject
                    {
                        [Items] = items,
                        ["pageNumber"] = pageNumber,
                    };

                    pages.Add(pagination);

                    var path = GeneratePagedPath(document.Path, pageNumber);

                    // TODO: the data of the new document is cloned which will also clone all global data.
                    // This ideally should only clone the document's own data instead

                    var newDocument = new CopyDocument(document, options.OutputFileSystem, path);
                    newDocument.Data = newDocument.Data.Clone().AsObject();
                    newDocument.Data["pagination"] = pagination;

                    if (aliasName != null)
                    {
                        newDocument.Data[aliasName] = size == 1 ? items[0] : items;
                    }

                    result.Add(new DocumentResult($"Generating file {path}", newDocument));
                }
            }
        });

        var orderedPages = pages.OrderBy(x => x["pageNumber"]?.GetValue<int>()).ToArray();
        var allPages = new NodeArray(orderedPages);

        for (var i = 0; i < allPages.Count; i++)
        {
            var page = allPages[i]!;

            page["pages"] = allPages;
            page["page"] = new NodeObject
            {
                ["next"] = i < allPages.Count - 1 ? allPages[i + 1] : null,
                ["previous"] = i > 0 ? allPages[i - 1] : null,
                ["first"] = allPages[0],
                ["last"] = allPages[allPages.Count - 1],
            };
        }

        return Task.FromResult<IEnumerable<IResult>>(result);
    }

    private FPath GeneratePagedPath(FPath path, int page)
    {
        // paged/index.html
        // paged/1/index.html


        if (Path.GetFileNameWithoutExtension(path.FullName) != "index")
        {
            path = page == 0
                ? FPath.Combine(path.GetDirectory().ToAbsolute(), Path.GetFileNameWithoutExtension(path.FullName) ?? "", "index.html")
                : FPath.Combine(path.GetDirectory().ToAbsolute(), Path.GetFileNameWithoutExtension(path.FullName) ?? "", page.ToString(), "index.html")
                ;
        }
        else
        {
            path = page == 0
                ? FPath.Combine(path.GetDirectory().ToAbsolute(), "index.html")
                : FPath.Combine(path.GetDirectory().ToAbsolute(), page.ToString(), "index.html")
                ;
        }

        return path;
    }
}
