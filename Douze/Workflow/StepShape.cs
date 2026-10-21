namespace Douze.Workflow;

/// <summary>
/// How the scheduler feeds a step: one file at a time, one group at a time, or everything at once.
/// </summary>
internal enum StepShape { Map, Group, Reduce }
