using Douze.Results;
using System.Text.Json.Nodes;

namespace Douze.FileProcessors
{
    internal sealed class JsonDataFileProcessor : IFileProcessor
    {
        public async IAsyncEnumerable<IResult> ProcessFileAsync(string path, Options options, JsonObject data)
        {
            using (var reader = File.OpenRead(path))
            {
                var document = JsonNode.Parse(reader)?.AsObject();

                if (document != null)
                {
                    JsonHelper.Merge(data, document);
                }
            }

            await Task.Yield();

            yield return new NullResult($"Reading json data file {path}");
        }
    }
}
