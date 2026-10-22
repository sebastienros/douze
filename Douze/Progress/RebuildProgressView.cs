using System.Globalization;
using System.Text;
using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Progress;

/// <summary>
/// The view of the builds that a watch session runs after a change: messages at Info and above, then one line, the
/// errors of a failed build under it. The steps are not listed: a rebuild runs few units.
/// </summary>
internal sealed class RebuildProgressView : IProgressView
{
    private readonly TextWriter _output;
    private readonly string _change;
    private readonly Lock _lock = new();

    /// <param name="output">Where the lines go.</param>
    /// <param name="change">What started the build: <c>_content/index.md changed</c>.</param>
    public RebuildProgressView(TextWriter output, string change)
    {
        _output = output;
        _change = change;
    }

    public void BuildStarted(WorkflowGraph graph) { }
    public void StepStarted(StepRun run) { }
    public void StepProgress(StepRun run) { }
    public void StepCompleted(StepRun run) { }

    public void Message(LogLevel level, string? step, string message)
    {
        if (level == LogLevel.Debug)
        {
            return;
        }

        var prefix = level switch
        {
            LogLevel.Warning => "warning: ",
            LogLevel.Error => "error: ",
            _ => "",
        };

        WriteLine(step == null ? $"{prefix}{message}" : $"{prefix}{step}: {message}");
    }

    public void BuildCompleted(BuildResult result)
    {
        WriteLine(Headline(result, _change));
    }

    /// <summary>
    /// <c>_content/index.md changed: 1 file written, 1 deleted in 0.12 s, 8 units executed, 212 cached</c>, or how the
    /// build failed, with its errors.
    /// </summary>
    public static string Headline(BuildResult result, string change)
    {
        var seconds = result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);

        if (result.Cancelled)
        {
            return $"{change}: build cancelled after {seconds} s";
        }

        if (result.Errors.Count > 0)
        {
            var builder = new StringBuilder($"{change}: build failed with {StepSummary.Plural(result.Errors.Count, "error")} in {seconds} s");

            foreach (var error in result.Errors)
            {
                builder.AppendLine();
                builder.Append(error.ToString());
            }

            return builder.ToString();
        }

        var written = result.Steps.Where(x => x.Node.Definition.Uses == "write").Sum(x => x.UnitsExecuted);
        var deleted = result.Deleted.Count > 0 ? $", {result.Deleted.Count} deleted" : "";
        var cached = result.UnitsReplayed > 0 ? $", {result.UnitsReplayed} cached" : "";

        return $"{change}: {StepSummary.Plural(written, "file")} written{deleted} in {seconds} s, {StepSummary.Plural(result.UnitsExecuted, "unit")} executed{cached}";
    }

    public void Dispose()
    {
        _output.Flush();
    }

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            _output.WriteLine(line);
        }
    }
}
