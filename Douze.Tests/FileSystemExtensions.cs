using SharpYaml.Serialization;
using Zio;

namespace Douze.Tests
{
    internal static class FileSystemExtensions
    {
        public static void CreateTemplate(this IFileSystem fs, UPath path, string content, object? data = null)
        {
            var directory = path.GetDirectory();

            if (directory != UPath.Root)
            {
                fs.CreateDirectory(directory);
            }

            if (data != null)
            {
                fs.WriteAllText(path, $"---\n{new Serializer().Serialize(data)}---\n");
            }

            fs.AppendAllText(path, content);
        }
    }
}
