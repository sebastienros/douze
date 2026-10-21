using System.Diagnostics;

namespace Douze.Tracing;

// StartTicks: Stopwatch ticks since the session started.
// DurationTicks: Stopwatch ticks; 0 for instant events.
internal sealed record TraceEvent(string Name, string Category, string? Unit, long StartTicks, long DurationTicks, int ThreadId, IReadOnlyList<KeyValuePair<string, string>> Args, bool IsInstant = false)
{
    public TimeSpan Start => Stopwatch.GetElapsedTime(0, StartTicks);

    public TimeSpan Duration => Stopwatch.GetElapsedTime(0, DurationTicks);
}

/// <summary>
/// The thread safe, append-only list of events recorded during a build.
/// </summary>
internal sealed class TraceSession
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly Lock _lock = new();
    private readonly List<TraceEvent> _events = [];

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// A snapshot of the events, in the order they ended.
    /// </summary>
    public IReadOnlyList<TraceEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    internal long Now() => Stopwatch.GetTimestamp() - _origin;

    internal void Add(TraceEvent traceEvent)
    {
        lock (_lock)
        {
            _events.Add(traceEvent);
        }
    }
}
