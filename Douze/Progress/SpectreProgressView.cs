using System.Diagnostics;
using System.Globalization;
using Douze.Workflow;
using Douze.Workflow.Scheduling;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Douze.Progress;

/// <summary>
/// The view of an interactive terminal: a live table of the running steps, one line per completed step above it, and
/// a summary table with a timeline of the steps at the end.
/// </summary>
/// <remarks>
/// The scheduler calls the view from many threads; they only update the state under a lock. One loop owns the
/// console: it prints the queued lines and redraws the table at most 20 times per second. A console that is not
/// interactive gets the lines and the summary only.
/// </remarks>
internal sealed class SpectreProgressView : IProgressView
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);

    private readonly IAnsiConsole _console;
    private readonly bool _verbose;
    private readonly bool _live;
    private readonly bool _unicode;
    private readonly Lock _lock = new();
    private readonly List<StepRun> _running = [];
    private readonly Queue<IRenderable> _lines = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = new();
    private Task? _display;
    private int _nameWidth = 12;
    private int _steps;
    private int _completed;

    public SpectreProgressView(IAnsiConsole console, bool verbose = false)
    {
        _console = console;
        _verbose = verbose;
        _live = console.Profile.Capabilities.Interactive && console.Profile.Capabilities.Ansi;
        _unicode = console.Profile.Capabilities.Unicode;
    }

    public void BuildStarted(WorkflowGraph graph)
    {
        lock (_lock)
        {
            _nameWidth = StepSummary.NameWidth(graph);
            _steps = graph.Nodes.Count;
            _clock.Start();

            if (_live && _display == null)
            {
                _display = Task.Run(DisplayAsync);
            }
        }
    }

    public void StepStarted(StepRun run)
    {
        lock (_lock)
        {
            _running.Add(run);
        }
    }

    public void StepProgress(StepRun run)
    {
        // The live table reads the counters of the running steps when it redraws.
    }

    public void StepCompleted(StepRun run)
    {
        var (marker, color) = run.State switch
        {
            StepState.Succeeded => (_unicode ? "✓" : "ok", "green"),
            StepState.Failed => (_unicode ? "✗" : "FAIL", "red"),
            StepState.Cancelled => (_unicode ? "-" : "skip", "grey"),
            _ => (_unicode ? "-" : "skip", "yellow"),
        };

        lock (_lock)
        {
            _running.Remove(run);
            _completed++;
            Enqueue(new Markup($"[{color}]{marker}[/] {Markup.Escape(StepSummary.Line(run, _nameWidth))}\n"));
        }
    }

    public void Message(LogLevel level, string? step, string message)
    {
        if (level == LogLevel.Debug && !_verbose)
        {
            return;
        }

        var prefix = level switch
        {
            LogLevel.Warning => "[yellow]warning:[/] ",
            LogLevel.Error => "[red]error:[/] ",
            LogLevel.Debug => "[grey]debug:[/] ",
            _ => "",
        };

        var text = Markup.Escape(step == null ? message : $"{step}: {message}");

        lock (_lock)
        {
            Enqueue(new Markup($"{prefix}{text}\n"));
        }
    }

    public void BuildCompleted(BuildResult result)
    {
        StopDisplay();

        _console.Write(Summary(result));

        var color = result.Succeeded ? "green" : result.Cancelled ? "grey" : "red";
        _console.Write(new Markup($"[{color}]{Markup.Escape(StepSummary.Headline(result))}[/]\n"));

        foreach (var error in result.Errors)
        {
            _console.Write(new Markup($"[red]{Markup.Escape(error.ToString())}[/]\n"));

            if (_verbose && error.Exception != null)
            {
                _console.Write(new Text(error.Exception + "\n", new Style(Color.Grey)));
            }
        }
    }

    public void Dispose()
    {
        StopDisplay();
        _stop.Dispose();
    }

    /// <summary>
    /// The table of the running steps, as the live display draws it.
    /// </summary>
    internal IRenderable RunningTable()
    {
        StepRun[] running;
        int completed;

        lock (_lock)
        {
            running = [.. _running.OrderBy(x => x.Node.Index)];
            completed = _completed;
        }

        var table = new Table().NoBorder().HideHeaders()
            .AddColumn("")
            .AddColumn("step")
            .AddColumn("shape")
            .AddColumn(new TableColumn("units").RightAligned())
            .AddColumn("bar")
            .AddColumn(new TableColumn("time").RightAligned());

        var frames = _unicode ? Spinner.Known.Dots : Spinner.Known.Line;
        var frame = frames.Frames[(int)(_clock.ElapsedMilliseconds / (long)frames.Interval.TotalMilliseconds % frames.Frames.Count)];
        var now = DateTimeOffset.UtcNow;

        foreach (var run in running)
        {
            var done = run.UnitsDone;
            var units = run.UnitsFinal ? run.Units : (int?)null;
            var elapsed = run.StartedAt is { } start ? now - start : TimeSpan.Zero;

            table.AddRow(
                new Markup($"[green]{Markup.Escape(frame)}[/]"),
                new Text(run.Node.Definition.Name),
                new Text(Shape(run), new Style(Color.Grey)),
                new Text(units is { } total ? $"{done}/{total}" : $"{done}/?"),
                Bar(done, units),
                new Text(Seconds(elapsed)));
        }

        table.Caption(new TableTitle($"{completed}/{_steps} steps done, {Seconds(_clock.Elapsed)}", new Style(Color.Grey)));
        return table;
    }

    /// <summary>
    /// One row per step with its counters and a bar on a common time scale.
    /// </summary>
    internal IRenderable Summary(BuildResult result)
    {
        var started = result.Steps.Where(x => x.StartedAt != null).ToArray();
        var origin = started.Length == 0 ? DateTimeOffset.MinValue : started.Min(x => x.StartedAt!.Value);
        var end = started.Length == 0 ? origin : started.Max(x => x.EndedAt ?? x.StartedAt!.Value);
        var span = Math.Max((end - origin).TotalSeconds, 1e-6);
        var width = Math.Clamp(_console.Profile.Width - _nameWidth - 59, 10, 60);

        var endLabel = Seconds(end - origin);
        var axis = "0".PadRight(Math.Max(width - endLabel.Length, 2)) + endLabel;

        var table = new Table().Border(TableBorder.Simple)
            .AddColumn("Step")
            .AddColumn("State")
            .AddColumn(new TableColumn("In").RightAligned())
            .AddColumn(new TableColumn("Out").RightAligned())
            .AddColumn(new TableColumn("Units").RightAligned())
            .AddColumn(new TableColumn("Cached").RightAligned())
            .AddColumn(new TableColumn("Time").RightAligned())
            .AddColumn(new TableColumn(new Text(axis, new Style(Color.Grey))).Width(width).NoWrap());

        foreach (var run in result.Steps)
        {
            var color = StateColor(run.State);
            var ran = run.State is StepState.Succeeded or StepState.Failed && run.StartedAt != null;

            table.AddRow(
                new Text(run.Node.Definition.Name),
                new Text(run.State.ToString().ToLowerInvariant(), new Style(color)),
                new Text(Count(run.FilesIn)),
                new Text(Count(run.FilesOut)),
                new Text(Count(run.Units)),
                new Text(Count(run.UnitsReplayed)),
                new Text(ran ? Seconds(StepSummary.Duration(run)) : ""),
                ran
                    ? new Text(TimelineBar((run.StartedAt!.Value - origin).TotalSeconds / span, ((run.EndedAt ?? run.StartedAt!.Value) - origin).TotalSeconds / span, width), new Style(color))
                    : Text.Empty);
        }

        return table;
    }

    /// <summary>
    /// A bar from <paramref name="start"/> to <paramref name="end"/> (fractions of the build) on <paramref name="width"/>
    /// characters. The end has a precision of an eighth of a character, and a bar is never empty.
    /// </summary>
    internal string TimelineBar(double start, double end, int width)
    {
        var from = Math.Clamp((int)Math.Round(start * width), 0, width - 1);
        var eighths = Math.Max(1, (int)Math.Round((end - start) * width * 8));
        eighths = Math.Min(eighths, (width - from) * 8);

        if (!_unicode)
        {
            return new string(' ', from) + new string('#', Math.Max(1, eighths / 8));
        }

        var bar = new string(' ', from) + new string('█', eighths / 8);
        return eighths % 8 == 0 ? bar : bar + "▏▎▍▌▋▊▉"[eighths % 8 - 1];
    }

    private async Task DisplayAsync()
    {
        await _console.Live(Text.Empty)
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Top)
            .StartAsync(async context =>
            {
                while (true)
                {
                    var stopping = _stop.IsCancellationRequested;

                    var lines = DrainLines();

                    if (lines.Length > 0)
                    {
                        // Lines are written over the live area without clearing it: blank it first (its shape only
                        // grows, so an empty target draws blank lines), or a short line would keep the end of a row.
                        context.UpdateTarget(Text.Empty);

                        foreach (var line in lines)
                        {
                            _console.Write(line);
                        }
                    }

                    context.UpdateTarget(stopping ? Text.Empty : RunningTable());

                    if (stopping)
                    {
                        return;
                    }

                    try
                    {
                        await Task.Delay(RefreshInterval, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // One more pass prints the last lines and clears the table.
                    }
                }
            });
    }

    private void StopDisplay()
    {
        Task? display;

        lock (_lock)
        {
            display = _display;

            if (display == null)
            {
                return;
            }

            _stop.Cancel();
        }

        display.GetAwaiter().GetResult();

        // Lines that arrived during the last pass of the display.
        lock (_lock)
        {
            _display = null;

            while (_lines.TryDequeue(out var line))
            {
                _console.Write(line);
            }
        }
    }

    // Called under the lock. Without a live display, lines are written at once.
    private void Enqueue(IRenderable line)
    {
        if (_display != null)
        {
            _lines.Enqueue(line);
        }
        else
        {
            _console.Write(line);
        }
    }

    private IRenderable[] DrainLines()
    {
        lock (_lock)
        {
            var lines = _lines.ToArray();
            _lines.Clear();
            return lines;
        }
    }

    private IRenderable Bar(int done, int? units)
    {
        const int Width = 20;

        if (units is not { } total || total == 0)
        {
            return Text.Empty;
        }

        var filled = Math.Clamp(done * Width / total, 0, Width);
        var (on, off) = _unicode ? ('━', '─') : ('#', '-');

        return new Markup($"[green]{new string(on, filled)}[/][grey]{new string(off, Width - filled)}[/]");
    }

    private static string Shape(StepRun run) => run.Node.Step.EffectiveShape switch
    {
        StepShape.Map => "map",
        StepShape.Group => "group",
        _ => "reduce",
    };

    private static Color StateColor(StepState state) => state switch
    {
        StepState.Succeeded => Color.Green,
        StepState.Failed => Color.Red,
        StepState.Skipped => Color.Yellow,
        _ => Color.Grey,
    };

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
}
