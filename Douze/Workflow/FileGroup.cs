namespace Douze.Workflow;

/// <summary>
/// A unit of work of a group step. <see cref="Files"/> are sorted by path.
/// </summary>
internal sealed record FileGroup(string Key, IReadOnlyList<VirtualFile> Files);
