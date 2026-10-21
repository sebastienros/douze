namespace Douze.Workflow.Scheduling;

/// <summary>
/// The identity of a unit of work inside a step: the file path for map, the group key for group, "*" for reduce.
/// </summary>
internal readonly record struct UnitKey(string Step, string Unit)
{
    public const string ReduceUnit = "*";

    public override string ToString() => $"{Step}:{Unit}";
}
