using System.Collections.Concurrent;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Creates the 'collections' properties.
/// </summary>
internal class CollectionsProcessor : IFileProcessor
{
    internal static readonly string Collections = "collections";
    internal static readonly string All = "all";
    internal static readonly string Tags = "tags";
    internal static readonly string Data = "data";

    public Predicate<FPath> Match { get; set; } = _ => true;

    public CollectionsProcessor(Predicate<FPath> match)
    {
        Match = match;
    }

    public Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        var collections = new ConcurrentDictionary<string, ConcurrentBag<NodeObject>>();

        // The collection "all" contains all content documents

        var collectionsAll = new ConcurrentBag<NodeObject>();
        collections[All] = collectionsAll;

        Parallel.ForEach(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, (document, cancellationToken) =>
        {
            result.Add(new DocumentResult(document));

            if (!Match(document.Path))
            {
                return;
            }

            var page = new NodeObject
            {
                [Data] = document.Data
            };

            // All documents that are content are added to the "all" collection
            if (document.IsContent)
            {
                collectionsAll.Add(page);
            }

            if (document.Data.TryGetItem(Tags, out var tagsNode))
            {
                var tags = new List<string>();

                if (tagsNode is NodeValue tagsValue)
                {
                    tags = new List<string>() { tagsValue.GetValue<string>() };
                }
                else if (tagsNode is NodeArray tagsArray)
                {
                    tags = new List<string>(tagsArray.Select(x => x?.GetValue<string>() ?? ""));
                }

                foreach (var tag in tags)
                {
                    if (!String.IsNullOrWhiteSpace(tag))
                    {
                        var collection = collections.GetOrAdd(tag, t => new ConcurrentBag<NodeObject>());

                        collection.Add(page);
                    }
                }
            }
        });

        var collectionsObject = new NodeObject();

        foreach (var entry in collections)
        {
            collectionsObject[entry.Key] = new NodeArray(entry.Value.ToArray());
        }

        // Update each document since global data has already been merged
        foreach (var document in documents)
        {
            document.Data[Collections] = collectionsObject;
        }

        // Mostly for unit testing since global data is already consumed by documents and altering its state doesn't impact them
        options.GlobalData[Collections] = collectionsObject;

        return Task.FromResult<IEnumerable<IResult>>(result);
    }
}
