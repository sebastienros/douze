using Douze.Results;

namespace Douze.FileProcessors;

internal interface IFileProcessor
{
    IAsyncEnumerable<IResult> ProcessFileAsync(IDocument document, Options options);
}
