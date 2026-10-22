using Douze.FileSystems;
using Douze.Markdown;
using Douze.Workflow.Yaml;

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
    public FPath ShortcodesFolder { get; init; } = FPath.Combine(DefaultIncludesFolder, "shortcodes/");   // site.shortcodes, default <includes>/shortcodes/
    public string? Url { get; init; }                   // site.url
    public int? Parallelism { get; init; }              // site.parallelism, used when -p is not given
    public MarkdownOptions Markdown { get; init; } = MarkdownOptions.None;   // the markdown section of douze.yml

    /// <summary>
    /// Uses the folders of <paramref name="settings"/> (the <c>site</c> section of the workflow file), and for the ones
    /// it does not set, <c>/_content/</c>, <c>/_data/</c> and <c>/_includes/</c> when they exist in <paramref name="source"/>,
    /// the root otherwise, and <c>shortcodes/</c> in the includes folder. <paramref name="markdown"/> is the
    /// <c>markdown</c> section, <see cref="MarkdownOptions.None"/> when it is absent.
    /// </summary>
    public static async Task<SiteOptions> DetectAsync(IFileSystem source, IFileSystem output, string? sourceDir, string? outputDir, SiteSettings? settings = null, MarkdownOptions? markdown = null)
    {
        settings ??= SiteSettings.None;
        var includes = settings.IncludesFolder ?? await DetectAsync(source, DefaultIncludesFolder);

        return new SiteOptions
        {
            Source = source,
            Output = output,
            SourceDirectory = sourceDir,
            OutputDirectory = outputDir,
            ContentFolder = settings.ContentFolder ?? await DetectAsync(source, DefaultContentFolder),
            DataFolder = settings.DataFolder ?? await DetectAsync(source, DefaultDataFolder),
            IncludesFolder = includes,
            ShortcodesFolder = settings.ShortcodesFolder ?? FPath.Combine(includes, "shortcodes/"),
            Url = settings.Url,
            Parallelism = settings.Parallelism,
            Markdown = markdown ?? MarkdownOptions.None,
        };

        static async Task<FPath> DetectAsync(IFileSystem source, FPath folder)
        {
            return (await source.GetDirectoryEntryAsync(folder)).Exists ? folder : FPath.Root;
        }
    }
}
