namespace Douze.Workflow;

/// <summary>
/// One, many or zero files, without allocating a list for the common single file case.
/// </summary>
internal readonly struct FileList
{
    private readonly VirtualFile? _single;
    private readonly IReadOnlyList<VirtualFile>? _many;

    private FileList(VirtualFile? single, IReadOnlyList<VirtualFile>? many)
    {
        _single = single;
        _many = many;
    }

    public static readonly FileList None = default;

    public static FileList Of(VirtualFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new FileList(file, null);
    }

    public static FileList Of(IReadOnlyList<VirtualFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Count switch
        {
            0 => None,
            1 => new FileList(files[0], null),
            _ => new FileList(null, files),
        };
    }

    public int Count => _single != null ? 1 : _many?.Count ?? 0;

    public VirtualFile this[int index]
    {
        get
        {
            if (_single != null && index == 0)
            {
                return _single;
            }

            if (_many != null)
            {
                return _many[index];
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public IEnumerable<VirtualFile> AsEnumerable()
    {
        if (_single != null)
        {
            return [_single];
        }

        return _many ?? [];
    }
}

/// <summary>
/// What a group or reduce step returns. Only reduce steps may contribute scopes.
/// </summary>
internal sealed record StepOutput(FileList Files, SiteScope? Scope = null, IReadOnlyList<FolderScope>? Folders = null);
