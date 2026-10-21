using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Console;

internal enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Shows the progress of a build. Called concurrently by the scheduler.
/// </summary>
internal interface IProgressView : IDisposable
{
    void BuildStarted(WorkflowGraph graph);
    void StepStarted(StepRun run);
    void StepProgress(StepRun run);          // UnitsDone/Units changed; views throttle to 20 redraws per second
    void StepCompleted(StepRun run);
    void Message(LogLevel level, string? step, string message);
    void BuildCompleted(BuildResult result); // summary
}
