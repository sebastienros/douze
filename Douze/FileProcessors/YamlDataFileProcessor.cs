using System.Collections.Concurrent;
using Douze.Results;

namespace Douze.FileProcessors;

internal sealed class YamlDataFileProcessor : IFileProcessor
{
    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new ConcurrentBag<IResult>();

        await Parallel.ForEachAsync(documents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {

            if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".yml")
            {
                result.Add(new DocumentResult(document));
                return;
            }

            using var stream = await document.OpenAsync();
            using var reader = new StreamReader(stream);

            var yaml = YamlHelper.Parse(reader);

            document.Data.Merge(yaml?.AsObject());

            result.Add(new DocumentResult($"Reading yaml data file {document.Path}"));
        });

        return result;
    }
}
