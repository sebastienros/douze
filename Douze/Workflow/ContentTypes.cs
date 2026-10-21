using System.Collections.Frozen;
using Douze.FileSystems;

namespace Douze.Workflow;

/// <summary>
/// Content types by extension, for files whose <see cref="VirtualFile.ContentType"/> is not set.
/// </summary>
internal static class ContentTypes
{
    public const string Default = "application/octet-stream";

    private static readonly FrozenDictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".md"] = "text/markdown",
        [".liquid"] = "text/html",
        [".txt"] = "text/plain",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".pdf"] = "application/pdf",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string FromPath(FPath path)
    {
        return ByExtension.TryGetValue(path.GetExtensionWithDot(), out var type) ? type : Default;
    }
}
