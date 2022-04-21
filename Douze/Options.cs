using System.Collections.Concurrent;
using Douze.Configuration;
using Douze.FileProcessors;
using Douze.FileSystems;
using Douze.Nodes;
using Fluid;

namespace Douze;

internal sealed partial class Options
{
#if DEBUG
    public int MaxDegreeOfParallelism { get; set; } = 1;
#else
    public int MaxDegreeOfParallelism { get; set; } = -1;
#endif

    public static readonly FPath DefaultContentFolder = "/_content/";
    public static readonly FPath DefaultDataFolder = "/_data/";
    public static readonly FPath DefaultIncludesFolder = "/_includes/";

    public NodeObject SiteData { get; set; } = new NodeObject();

    /// <summary>
    /// Gets or sets the content folders relative to <see cref="SourceFileSystem"/>
    /// </summary>
    public FPath[] ContentFolders { get; set; } = new FPath[] { DefaultContentFolder };

    /// <summary>
    /// Gets or sets the data folders relative to <see cref="SourceFileSystem"/>
    /// </summary>
    public FPath[] DataFolders { get; set; } = new FPath[] { DefaultDataFolder };

    /// <summary>
    /// Gets or sets the includes folders relative to <see cref="SourceFileSystem"/>
    /// </summary>
    public FPath[] IncludesFolders { get; set; } = new FPath[] { DefaultIncludesFolder };

    public FluidParser FluidParser { get; } = new();

    // File processors that generate actual documents. This is the second pass on all documents.
    public List<IFileProcessor> ContentProcessors { get; } = new();

    // File processors that generate global data. This is the first pass on all documents.
    public List<IFileProcessor> DataProcessors { get; } = new();

    /// <summary>
    /// Gets a dictionary of items shared during the site generation
    /// </summary>
    public ConcurrentDictionary<PropertyBagKey, object?> PropertyBag { get; } = new();

    /// <summary>
    /// Gets or sets the file system rooted to the source folder.
    /// </summary>
    public IFileSystem SourceFileSystem { get; set; } = new MemoryFileSystem();

    /// <summary>
    /// Gets or sets the file system rooted to the output folder.
    /// </summary>
    public IFileSystem OutputFileSystem { get; set; } = new MemoryFileSystem();

    public Options(IFileSystem source, IFileSystem output, IEnumerable<string>? dataFolders = null, IEnumerable<string>? contentFolders = null, IEnumerable<string>? includesFolders = null) : this()
    {
        ArgumentNullException.ThrowIfNull(source, nameof(source));
        ArgumentNullException.ThrowIfNull(output, nameof(output));

        SourceFileSystem = source;
        OutputFileSystem = output;

        if (contentFolders != null && contentFolders.Any())
        {
            ContentFolders = contentFolders.Select(x => new FPath(x + FPath.DirectorySeparator, true).ToAbsolute()).ToArray();
        }

        if (dataFolders != null && dataFolders.Any())
        {
            DataFolders = dataFolders.Select(x => new FPath(x + FPath.DirectorySeparator, true).ToAbsolute()).ToArray();
        }

        if (includesFolders != null && includesFolders.Any())
        {
            IncludesFolders = includesFolders.Select(x => new FPath(x + FPath.DirectorySeparator, true).ToAbsolute()).ToArray();
        }
    }

    public void BuildDefaultPipeline()
    {
        ContentProcessors.Add(new MarkdownFileProcessor(IncludeRule.ParseRules("*.md")));
        ContentProcessors.Add(new UrlFileProcessor(IncludeRule.ParseRules("*.md", "*.html")));
        ContentProcessors.Add(new PaginationProcessor(IncludeRule.ParseRules("*.html")));
        ContentProcessors.Add(new CollectionsProcessor(IncludeRule.ParseRules("*.html")));
        ContentProcessors.Add(new LiquidTemplateFileProcessor(IncludeRule.ParseRules("*.html", "*.json")));
        ContentProcessors.Add(new LayoutFileProcessor(IncludeRule.ParseRules("*.html")));
        ContentProcessors.Add(new CopyDocumentProcessor(IncludeRule.ParseRules("*.jpg", "*.gif", "*.png")));

        DataProcessors.Add(new XmlDataFileProcessor());
        DataProcessors.Add(new YamlDataFileProcessor());
        DataProcessors.Add(new JsonDataFileProcessor());
        DataProcessors.Add(new PageMetadataProcessor());
        DataProcessors.Add(new FrontMatterProcessor(IncludeRule.ParseRules("*.md", "*.html", "*.liquid")));
    }

    public readonly record struct PropertyBagKey(string Category, string Key);
}
