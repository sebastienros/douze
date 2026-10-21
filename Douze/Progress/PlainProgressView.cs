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
        _nameWidth = Math.Max(_nameWidth, graph.Nodes.Count == 0 ? 0 : graph.Nodes.Max(x => x.Definition.Name.Length) + 2);
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
        var name = run.Node.Definition.Name.PadRight(_nameWidth);

        var line = run.State switch
        {
            StepState.Succeeded => string.Create(CultureInfo.InvariantCulture, $"{_ok} {name}{run.FilesConsumed,6} files {Seconds(run),7:0.00} s"),
            StepState.Failed => $"{_fail} {name}{FirstError(run)}",
            StepState.Cancelled => $"{_skip} {name}cancelled",
            _ => $"{_skip} {name}skipped",
        };

        WriteLine(line);
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
        var seconds = result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();

        if (result.Cancelled)
        {
            builder.Append($"Build cancelled after {seconds} s");
        }
        else if (result.Errors.Count > 0)
        {
            builder.Append($"Build failed with {Plural(result.Errors.Count, "error")} in {seconds} s");
        }
        else
        {
            builder.Append($"Build succeeded: {Plural(result.Steps.Count, "step")}, {Plural(result.Outputs.Count, "file")} written in {seconds} s");
        }

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

    private static double Seconds(StepRun run)
    {
        return run.StartedAt is { } start && run.EndedAt is { } end ? (end - start).TotalSeconds : 0;
    }

    private static string FirstError(StepRun run)
    {
        if (run.Errors.Count == 0)
        {
            return "failed";
        }

        var first = run.Errors[0].ToString();
        return run.Errors.Count == 1 ? first : $"{first} (and {Plural(run.Errors.Count - 1, "more error")})";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            _output.WriteLine(line);
        }
    }
}
