namespace Douze.Nodes;

/// <summary>
/// Specifies how nodes are merged together.
/// </summary>
internal class MergeSettings
{
    public static MergeSettings Default = new() { MergeArrayHandling = MergeArrayHandling.Union, MergeObjectHandling = MergeObjectHandling.Recursive };

    public MergeArrayHandling MergeArrayHandling { get; set; }
    public MergeObjectHandling MergeObjectHandling { get; set; }
}

/// <summary>
/// Specifies how arrays are merged together.
/// </summary>
public enum MergeArrayHandling
{
    /// <summary>Concatenate arrays.</summary>
    Concat = 0,

    /// <summary>Union arrays, skipping items that already exist.</summary>
    Union = 1,

    /// <summary>Replace all array items.</summary>
    Replace = 2,

    /// <summary>Merge array items together, matched by index.</summary>
    Merge = 3,
}

/// <summary>
/// Specifies how objects are merged together.
/// </summary>
public enum MergeObjectHandling
{
    /// <summary>Merge all object items recursively.</summary>
    Recursive = 0,

    /// <summary>Union object, skipping items that already exist.</summary>
    Union = 1,
}
