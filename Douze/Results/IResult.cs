namespace Douze.Results;

internal interface IResult
{
    /// <summary>
    /// Gets the description of the file.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the relative path of the file.
    /// </summary>
    string? FilePath { get; }

    /// <summary>
    /// Gets the content-type of the file.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// Saves the file.
    /// </summary>
    /// <param name="outputStream"></param>
    /// <returns></returns>
    Task SaveFileAsync(Stream outputStream);
}
