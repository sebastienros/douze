using System.Xml.Linq;
using Douze.Results;

namespace Douze.FileProcessors;

/// <summary>
/// Assigns the XML data of a document to its Data property.
/// </summary>
internal sealed class XmlDataFileProcessor : IFileProcessor
{
    public async Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options)
    {
        var result = new List<IResult>();
        var filteredDocuments = new List<IDocument>();

        foreach (var document in documents)
        {
            if (document.Path.GetExtensionWithDot().ToLowerInvariant() != ".xml")
            {
                result.Add(new DocumentsResult(document));
            }
            else
            {
                document.DocumentType = DocumentTypes.Data;
                result.Add(new DocumentsResult($"Reading xml data file {document.Path}", document));
                filteredDocuments.Add(document);
            }
        }

        await Parallel.ForEachAsync(filteredDocuments, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, async (document, cancellationToken) =>
        {
            using var stream = await document.OpenAsync();

            var xml = await XDocument.LoadAsync(stream, LoadOptions.None, default);

            if (xml != null)
            {
                var o = XmlHelper.Convert(xml)?.AsObject();

                if (o != null)
                {
                    document.Data.Merge(o);
                }
            }
        });

        return result;
    }
}
