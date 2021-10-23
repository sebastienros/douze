using Douze.Results;

namespace Douze.FileProcessors;

internal sealed class YamlDataFileProcessor : IFileProcessor
{
    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".yml")
        {
            yield return new DocumentResult(document);
            yield break;
        }

        using var stream = await document.OpenAsync();
        using var reader = new StreamReader(stream);

        YamlHelper.MergeData(document.Data, reader);

        yield return new DocumentResult($"Reading yaml data file {document.Path}");
    }
}
