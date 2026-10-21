namespace Douze.Tracing;

/// <summary>
/// Records spans and instant events of a build.
/// </summary>
internal interface ITraceRecorder
{
    // category: <c>build</c>, <c>step</c>, <c>unit</c>, <c>command</c> or <c>io</c>.
    TraceSpan Begin(string name, string category, string? unit = null);

    void Instant(string name, string category, string? args = null);

    TraceSession Session { get; }
}

/// <summary>
/// A span being recorded. <see cref="Dispose"/> ends it. A default instance records nothing.
/// </summary>
internal readonly struct TraceSpan : IDisposable
{
    private readonly TraceSpanState? _state;

    internal TraceSpan(TraceSpanState state)
    {
        _state = state;
    }

    public bool IsRecording => _state != null;

    public void AddArg(string key, string value) => _state?.AddArg(key, value);

    public void Dispose() => _state?.End();
}

internal sealed class TraceSpanState
{
    private readonly TraceSession _session;
    private readonly string _name;
    private readonly string _category;
    private readonly string? _unit;
    private readonly long _start;
    private readonly int _threadId;
    private List<KeyValuePair<string, string>>? _args;
    private int _ended;

    public TraceSpanState(TraceSession session, string name, string category, string? unit)
    {
        _session = session;
        _name = name;
        _category = category;
        _unit = unit;
        _threadId = Environment.CurrentManagedThreadId;
        _start = session.Now();
    }

    public void AddArg(string key, string value)
    {
        lock (this)
        {
            (_args ??= []).Add(new(key, value));
        }
    }

    public void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return;
        }

        IReadOnlyList<KeyValuePair<string, string>> args;

        lock (this)
        {
            args = _args?.ToArray() ?? [];
        }

        _session.Add(new TraceEvent(_name, _category, _unit, _start, _session.Now() - _start, _threadId, args));
    }
}
