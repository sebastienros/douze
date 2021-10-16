namespace Douze.Results
{
    internal interface IResult
    {
        string Description { get; }
        string? FilePath { get; }
        string? ContentType { get; }
        Task SaveFileAsync(Stream outputStream);
    }
}
