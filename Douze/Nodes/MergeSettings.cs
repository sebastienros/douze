namespace Douze.Nodes;

/// <summary>
/// Specifies how nodes are merged together.
/// </summary>
internal class MergeSettings
{
    public static MergeSettings Default = new MergeSettings { MergeArrayHandling = MergeArrayHandling.Union };

    public MergeArrayHandling MergeArrayHandling { get; set; }
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
    Merge = 3
}
