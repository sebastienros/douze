using Douze.Results;

namespace Douze.FileProcessors;

internal interface IFileProcessor
{
    Task<IEnumerable<IResult>> ProcessFileAsync(IEnumerable<IDocument> documents, Options options);
}
