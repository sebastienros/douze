using Douze.FileSystems;

namespace Douze;

/// <summary>
/// The folders and file systems of a site.
/// </summary>
internal sealed class SiteOptions
{
    public static readonly FPath DefaultContentFolder = "/_content/";
    public static readonly FPath DefaultDataFolder = "/_data/";
    public static readonly FPath DefaultIncludesFolder = "/_includes/";

    public required IFileSystem Source { get; init; }
    public required IFileSystem Output { get; init; }
    public string? OutputDirectory { get; init; }       // absolute disk path when Output is physical; expands {{ site }} in commands
    public string? SourceDirectory { get; init; }       // absolute disk path when Source is physical; default cwd for commands
    public FPath ContentFolder { get; init; } = DefaultContentFolder;   // FPath.Root when the folder does not exist
    public FPath DataFolder { get; init; } = DefaultDataFolder;
    public FPath IncludesFolder { get; init; } = DefaultIncludesFolder;
    public string? Url { get; init; }                   // site.url, exposed to templates (phase 2)

    /// <summary>
    /// Uses <c>/_content/</c>, <c>/_data/</c> and <c>/_includes/</c> when they exist in <paramref name="source"/>, the root otherwise.
    /// </summary>
    public static async Task<SiteOptions> DetectAsync(IFileSystem source, IFileSystem output, string? sourceDir, string? outputDir)
    {
        return new SiteOptions
        {
            Source = source,
            Output = output,
            SourceDirectory = sourceDir,
            OutputDirectory = outputDir,
            ContentFolder = await DetectAsync(source, DefaultContentFolder),
            DataFolder = await DetectAsync(source, DefaultDataFolder),
            IncludesFolder = await DetectAsync(source, DefaultIncludesFolder),
        };

        static async Task<FPath> DetectAsync(IFileSystem source, FPath folder)
        {
            return (await source.GetDirectoryEntryAsync(folder)).Exists ? folder : FPath.Root;
        }
    }
}
