using Douze.FileSystems;
using Douze.Results;
using System.Text.Json.Nodes;

namespace Douze.FileProcessors;

internal sealed class YamlDataFileProcessor : IFileProcessor
{
    public async IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data)
    {
        using var stream = await file.FileSystem.OpenFileAsync(file.Path);
        using var reader = new StreamReader(stream);
        YamlHelper.MergeData(data, reader);

        await Task.Yield();

        yield return new NullResult($"Reading yaml data file {file.Path}");
    }
}
