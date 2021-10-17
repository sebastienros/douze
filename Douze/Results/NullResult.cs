using Zio;

namespace Douze.Results;

internal sealed class NullResult : IResult
{
    public NullResult(string description)
    {
        Description = description;
    }

    public UPath FilePath => UPath.Empty;
    public string Description { get; }
    public string? ContentType => null;

    public Task SaveFileAsync(Stream outputStream)
    {
        return Task.CompletedTask;
    }
}
