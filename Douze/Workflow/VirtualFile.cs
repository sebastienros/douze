using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// A file flowing between steps. Immutable: steps derive new files with <see cref="With"/> or
/// <see cref="Derived"/>, or pass the same instance through.
/// </summary>
internal sealed class VirtualFile
{
    public VirtualFile(FPath path, FileContent content, Metadata metadata, string? contentType, Provenance provenance, DateTime? lastWriteTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(provenance);

        if (!path.IsAbsolute || !path.IsFile)
        {
            throw new ArgumentException($"A virtual file needs an absolute file path: '{path}'", nameof(path));
        }

        Path = path;
        Content = content;
        Metadata = metadata;
        ContentType = contentType;
        Provenance = provenance;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public FPath Path { get; }

    public FileContent Content { get; }

    public Metadata Metadata { get; }

    /// <summary>
    /// The content type, or <c>null</c> to derive it from the extension.
    /// </summary>
    public string? ContentType { get; }

    public Provenance Provenance { get; }

    /// <summary>
    /// The modification time of the source file this file comes from, used for <c>page.date</c>. Kept through
    /// <see cref="With"/>; null for files derived from several inputs.
    /// </summary>
    public DateTime? LastWriteTimeUtc { get; }

    public Hash128 ContentHash => Content.Hash;

    public Hash128 MetadataHash => Metadata.Hash;

    public FileRef Ref => new(Path, ContentHash, MetadataHash);

    /// <summary>
    /// Derives a new file from this one. Its provenance is <paramref name="step"/> with this file's ref as the only input.
    /// </summary>
    public VirtualFile With(string step, FPath? path = null, FileContent? content = null, Metadata? metadata = null, string? contentType = null)
    {
        return new VirtualFile(
            path ?? Path,
            content ?? Content,
            metadata ?? Metadata,
            contentType ?? ContentType,
            new Provenance(step, [Ref]),
            LastWriteTimeUtc);
    }

    /// <summary>
    /// Creates a file derived from several inputs (group and reduce steps).
    /// </summary>
    public static VirtualFile Derived(string step, IReadOnlyList<VirtualFile> inputs, FPath path, FileContent content, Metadata metadata, string? contentType)
    {
        var refs = ImmutableArray.CreateBuilder<FileRef>(inputs.Count);

        foreach (var input in inputs)
        {
            refs.Add(input.Ref);
        }

        return new VirtualFile(path, content, metadata, contentType, new Provenance(step, refs.MoveToImmutable()));
    }

    /// <summary>
    /// Creates a source file whose content is read on first access.
    /// </summary>
    public static VirtualFile FromSource(IFileSystem fs, FPath path, FileMetadata info)
    {
        path = path.ToAbsolute();

        return new VirtualFile(
            path,
            FileContent.FromFile(fs, path, info.Length),
            new Metadata(new NodeObject(), path.GetDirectory()),
            contentType: null,
            Provenance.Source,
            info.LastWriteTimeUtc);
    }

    public override string ToString() => Path.FullName;
}

internal readonly record struct FileRef(FPath Path, Hash128 ContentHash, Hash128 MetadataHash);
