namespace Douze.Results;

internal class DocumentResult : IResult
{
    public DocumentResult(params IDocument[] documents)
    {
        Documents = documents;
        Description = null;
    }

    public DocumentResult(string description, params IDocument[] documents)
    {
        Documents = documents;
        Description = description;
    }

    public IEnumerable<IDocument> Documents { get; set; }

    public string? Description { get; set; }

    public ValueTask ExecuteAsync(List<IDocument> documents)
    {
        documents.AddRange(Documents);

        return ValueTask.CompletedTask;
    }
}
