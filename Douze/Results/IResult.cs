namespace Douze.Results;

internal interface IResult
{
    string? Description { get; }
    ValueTask ExecuteAsync(List<IDocument> documents);
}
