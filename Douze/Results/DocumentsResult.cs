namespace Douze.Results;

internal class DocumentsResult : IResult
{
    public DocumentsResult(params IDocument[] documents)
    {
        Documents = documents;
        Description = null;
    }

    public DocumentsResult(IDocument document)
    {
        Document = document;
        Description = null;
    }

    public DocumentsResult(string description, params IDocument[] documents)
    {
        Documents = documents;
        Description = description;
    }

    public DocumentsResult(string description, IDocument document)
    {
        Document = document;
        Description = description;
    }

    public IEnumerable<IDocument>? Documents { get; set; }
    public IDocument? Document { get; set; }

    public string? Description { get; set; }

    public ValueTask ExecuteAsync(List<IDocument> documents)
    {
        if (Documents != null)
        {
            documents.AddRange(Documents);
        }

        if (Document != null)
        {
            documents.Add(Document);
        }

        return ValueTask.CompletedTask;
    }
}
