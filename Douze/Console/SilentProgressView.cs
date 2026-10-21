using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Console;

/// <summary>
/// Prints errors only (<c>--quiet</c>, and the default of tests).
/// </summary>
internal sealed class SilentProgressView : IProgressView
{
    private readonly TextWriter? _error;
    private readonly Lock _lock = new();

    /// <param name="error">Where errors go; <c>null</c> discards them.</param>
    public SilentProgressView(TextWriter? error = null)
    {
        _error = error;
    }

    public static SilentProgressView Discard { get; } = new();

    public void BuildStarted(WorkflowGraph graph) { }
    public void StepStarted(StepRun run) { }
    public void StepProgress(StepRun run) { }
    public void StepCompleted(StepRun run) { }

    public void Message(LogLevel level, string? step, string message)
    {
        if (level == LogLevel.Error)
        {
            WriteLine(step == null ? $"error: {message}" : $"error: {step}: {message}");
        }
    }

    public void BuildCompleted(BuildResult result)
    {
        foreach (var error in result.Errors)
        {
            WriteLine(error.ToString());
        }
    }

    public void Dispose()
    {
        _error?.Flush();
    }

    private void WriteLine(string line)
    {
        if (_error == null)
        {
            return;
        }

        lock (_lock)
        {
            _error.WriteLine(line);
        }
    }
}
