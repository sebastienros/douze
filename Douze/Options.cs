using Douze.FileProcessors;
using Fluid;
using Zio;
using Zio.FileSystems;

namespace Douze;

internal sealed partial class Options
{
#if DEBUG
    public int MaxDegreeOfParallelism { get; set; } = 1;
#else
    public int MaxDegreeOfParallelism { get; set; } = -1;
#endif

    /// <summary>
    /// Gets or sets the data folder relative to <see cref="SourceFileSystem"/>
    /// </summary>
    public UPath DataFolder { get; set; } = "/_data";

    /// <summary>
    /// Gets or sets the includes folder relative to <see cref="SourceFileSystem"/>
    /// </summary>
    public UPath IncludesFolder { get; set; } = "/_includes";

    public FluidParser FluidParser { get; } = new();
    public Dictionary<string, IFileProcessor> ContentMappings { get; } = new();
    public Dictionary<string, IFileProcessor> DataMappings { get; } = new();
    public Dictionary<string, object?> PropertyBag { get; } = new();

    /// <summary>
    /// Gets or sets the file system rooted to the source folder.
    /// </summary>
    public IFileSystem SourceFileSystem { get; set; } = new MemoryFileSystem();

    /// <summary>
    /// Gets or sets the file system rooted to the output folder.
    /// </summary>
    public IFileSystem OutputFileSystem { get; set; } = new MemoryFileSystem();

    public Options(IFileSystem source, IFileSystem output) : this()
    {
        ArgumentNullException.ThrowIfNull(source, nameof(source));
        ArgumentNullException.ThrowIfNull(output, nameof(output));

        SourceFileSystem = source;
        OutputFileSystem = output;
    }
}
