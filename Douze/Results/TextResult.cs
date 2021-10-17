namespace Douze.Results;

internal sealed class TextResult : IResult
{
    public TextResult(string filePath, string description, string content, string contentType)
    {
        FilePath = filePath;
        Description = description;
        Content = content;
        ContentType = contentType;
    }

    public string FilePath { get; }
    public string Description { get; }
    public string Content { get; }
    public string ContentType { get; }

    public async Task SaveFileAsync(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(nameof(outputStream));
            
        using (var sw = new StreamWriter(outputStream))
        {
            await sw.WriteAsync(Content);
        }
    }
}
