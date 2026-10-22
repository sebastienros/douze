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

    /// <summary>
    /// The paths of the files under <paramref name="folder"/>, sorted.
    /// </summary>
    public static string[] EnumerateFiles(this IFileSystem fs, FPath folder)
    {
        var entries = fs.EnumerateFileSystemEntriesAsync(folder, recursive: true, filter: null).AsTask().GetAwaiter().GetResult();
        return [.. entries.Where(x => !x.IsDirectory).Select(x => x.Path.ToAbsolute()).Where(x => x.IsInDirectory(folder)).Select(x => x.FullName).Order(StringComparer.Ordinal)];
    }

    public static async ValueTask<byte[]> ReadAllBytesAsync(this IFileSystem fs, FPath path)
    {
        await using var stream = await fs.OpenFileAsync(path);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
