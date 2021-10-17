using Douze.Results;
using System.Text.Json.Nodes;
using Zio;

namespace Douze.FileProcessors;

internal sealed class YamlDataFileProcessor : IFileProcessor
{
    public async IAsyncEnumerable<IResult> ProcessFileAsync(FileEntry file, Options options, JsonObject data)
    {
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        YamlHelper.MergeData(data, reader);

        await Task.Yield();

        yield return new NullResult($"Reading yaml data file {file.Path}");
    }
}
