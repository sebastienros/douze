using System.Text.Json.Nodes;
using Douze.FileSystems;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Implements
/// - FrontMatter
/// - Liquid pre-processing
/// - Layout chaining
/// - Permalink
/// </summary>
internal class PageMetadataProcessor : IFileProcessor
{
    public Predicate<FPath>? Match { get; set; }

    public async IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options)
    {
        // Pass-through
        yield return new DocumentResult(document);

        if ((Match == null || Match(document.Path)) && document is SourceDocument sourceDocument)
        {
            var metadata = await sourceDocument.FileSystem.GetFileMetadaAsync(document.Path);

            var page = document.Data["page"] as JsonObject;

            if (page == null)
            {
                document.Data["page"] = page = new JsonObject();
            }

            page.Add("date", metadata.LastWriteTimeUtc);
        }
    }
}
