using Douze.Results;
using System.Text.Json.Nodes;

namespace Douze
{
    internal class YamlDataFileProcessor : IFileProcessor
    {
        public async IAsyncEnumerable<IResult> ProcessFileAsync(string path, Options options, JsonObject data)
        {
            using (var reader = File.OpenText(path))
            {
                YamlHelper.MergeData(data, reader);
            }

            await Task.Yield();

            yield return new NullResult($"Reading yaml data file {path}");
        }
    }
}
