using System.Globalization;
using System.Text;
using Douze.Workflow;
using Douze.Workflow.Scheduling;

namespace Douze.Progress;

/// <summary>
/// Prints one line per completed step and a summary. With <c>verbose</c>, also one line per unit and stack traces.
/// </summary>
internal sealed class PlainProgressView : IProgressView
{
    private readonly TextWriter _output;
    private readonly bool _verbose;
    private readonly string _ok;
    private readonly string _fail;
    private readonly string _skip;
    private readonly Lock _lock = new();
    private int _nameWidth = 12;

    public PlainProgressView(TextWriter output, bool verbose = false, bool? unicode = null)
    {
        _output = output;
        _verbose = verbose;

        var useUnicode = unicode ?? Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage;
        _ok = useUnicode ? "✓" : "ok";
        _fail = useUnicode ? "✗" : "FAIL";
        _skip = useUnicode ? "-" : "skip";
    }

    public void BuildStarted(WorkflowGraph graph)
    {
        _nameWidth = StepSummary.NameWidth(graph);
    }

    public void StepStarted(StepRun run)
    {
    }

    public void StepProgress(StepRun run)
    {
        if (!_verbose)
        {
            return;
        }

        var total = run.UnitsFinal ? run.Units.ToString(CultureInfo.InvariantCulture) : "?";
        WriteLine($"  {run.Node.Definition.Name} {run.UnitsDone}/{total}");
    }

    public void StepCompleted(StepRun run)
    {
        var marker = run.State switch
        {
            StepState.Succeeded => _ok,
            StepState.Failed => _fail,
            _ => _skip,
        };

        WriteLine($"{marker} {StepSummary.Line(run, _nameWidth)}");
    }

    public void Message(LogLevel level, string? step, string message)
    {
        if (level == LogLevel.Debug && !_verbose)
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
        var builder = new StringBuilder(StepSummary.Headline(result));

        foreach (var error in result.Errors)
        {
            builder.AppendLine();
            builder.Append(error.ToString());

            if (_verbose && error.Exception != null)
            {
                builder.AppendLine();
                builder.Append(error.Exception);
            }
        }

        WriteLine(builder.ToString());
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
