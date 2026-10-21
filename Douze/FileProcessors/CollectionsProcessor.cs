using System.Collections.Concurrent;
using System.Globalization;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Creates the 'collections' properties.
/// Items are sorted by date ascending, using the 'date' property if defined, or 'page.date' otherwise, then by source path.
/// </summary>
internal sealed class CollectionsProcessor : IFileProcessor
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

        var collections = new ConcurrentDictionary<string, ConcurrentBag<CollectionItem>>();

        // The collection "all" contains all content documents

        var collectionsAll = new ConcurrentBag<CollectionItem>();
        collections[All] = collectionsAll;

        Parallel.ForEach(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, (document, cancellationToken) =>
        {
            result.Add(new DocumentsResult(document));

            if (!Match(document.Path))
            {
                return;
            }

            var page = new CollectionItem(new NodeObject
            {
                [Data] = document.Data
            }, GetDate(document.Data), document.Path);

            // All documents that are content are added to the "all" collection
            if (document.DocumentType == DocumentTypes.Content)
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
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var collection = collections.GetOrAdd(tag, t => new ConcurrentBag<CollectionItem>());

                        collection.Add(page);
                    }
                }
            }
        });

        var collectionsNode = new NodeObject();

        foreach (var entry in collections)
        {
            // Documents are processed in parallel so the order needs to be restored
            collectionsNode[entry.Key] = new NodeArray(entry.Value.OrderBy(x => x.Date).ThenBy(x => x.Path).Select(x => x.Page).ToArray());
        }

        options.SiteData[Collections] = collectionsNode;

        // Update each document
        foreach (var document in documents)
        {
            document.Data[Collections] = collectionsNode;
        }

        return Task.FromResult<IEnumerable<IResult>>(result);
    }

    private static DateTimeOffset GetDate(NodeObject data)
    {
        if (data.TryGetItem("date", out var date) && TryGetDate(date, out var result))
        {
            return result;
        }

        if (data.TryGetItem("page", out var page) && page is NodeObject pageObject && pageObject.TryGetItem("date", out var pageDate) && TryGetDate(pageDate, out result))
        {
            return result;
        }

        return DateTimeOffset.MinValue;
    }

    private static bool TryGetDate(Node? node, out DateTimeOffset date)
    {
        if (node is NodeValue value)
        {
            if (value.TryGetValue<DateTimeOffset>(out var dateTimeOffset))
            {
                date = dateTimeOffset;
                return true;
            }

            if (value.TryGetValue<DateTime>(out var dateTime))
            {
                date = dateTime.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(dateTime, TimeSpan.Zero) : new DateTimeOffset(dateTime);
                return true;
            }

            if (value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
            {
                return true;
            }
        }

        date = default;
        return false;
    }

    private sealed record CollectionItem(NodeObject Page, DateTimeOffset Date, FPath Path);
}
