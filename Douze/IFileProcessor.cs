using Douze.Results;
using System.Text.Json.Nodes;

namespace Douze
{
    internal interface IFileProcessor
    {
        IAsyncEnumerable<IResult> ProcessFileAsync(string path, Options options, JsonObject data);
    }
}
