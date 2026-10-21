namespace Douze.Tracing;

/// <summary>
/// Records build, step and command spans always, and unit spans only when <c>traceUnits</c> is set,
/// because thousands of spans of a few microseconds are noise in a timeline.
/// </summary>
internal sealed class TraceRecorder : ITraceRecorder
{
    public const string UnitCategory = "unit";

    private readonly bool _traceUnits;

    public TraceRecorder(bool traceUnits = false)
    {
        _traceUnits = traceUnits;
    }

    public TraceSession Session { get; } = new();

    public TraceSpan Begin(string name, string category, string? unit = null)
    {
        if (!_traceUnits && category == UnitCategory)
        {
            return default;
        }

        return new TraceSpan(new TraceSpanState(Session, name, category, unit));
    }

    public void Instant(string name, string category, string? args = null)
    {
        Session.Add(new TraceEvent(name, category, null, Session.Now(), 0, Environment.CurrentManagedThreadId,
            args == null ? [] : [new("args", args)], IsInstant: true));
    }
}
