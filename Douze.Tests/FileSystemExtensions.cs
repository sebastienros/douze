using System.IO;
using System.Threading.Tasks;
using Douze.FileSystems;
using SharpYaml;

namespace Douze.Tests;

internal static class FileSystemExtensions
{
    public static async ValueTask CreateTemplateAsync(this IFileSystem fs, FPath path, string content, object? data = null)
    {
        var directory = path.GetDirectory();

        await fs.CreateDirectoryAsync(directory);

        using var stream = await fs.CreateFileAsync(path);
        using var sw = new StreamWriter(stream);

        if (data != null)
        {
            await sw.WriteAsync($"---\n{YamlSerializer.Serialize(data)}---\n");
        }

        await sw.WriteAsync(content);
    }
}