namespace Douze.Tracing;

/// <summary>
/// A recorder that records nothing.
/// </summary>
internal sealed class NullTraceRecorder : ITraceRecorder
{
    public static readonly NullTraceRecorder Instance = new();

    public TraceSession Session { get; } = new();

    public TraceSpan Begin(string name, string category, string? unit = null) => default;

    public void Instant(string name, string category, string? args = null)
    {
    }
}
