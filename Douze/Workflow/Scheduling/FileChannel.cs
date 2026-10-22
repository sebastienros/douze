using System.Threading.Channels;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// The output of a node: one unbounded channel per consumer, so that every output reaches every dependent (fan-out).
/// </summary>
internal sealed class FileChannel
{
    private static readonly UnboundedChannelOptions Options = new() { SingleReader = true, SingleWriter = false };

    private readonly Dictionary<string, Channel<VirtualFile>> _channels;

    public FileChannel(IEnumerable<string> consumers)
    {
        _channels = consumers.Distinct(StringComparer.Ordinal).ToDictionary(x => x, _ => Channel.CreateUnbounded<VirtualFile>(Options), StringComparer.Ordinal);
    }

    public ChannelReader<VirtualFile> Subscribe(string consumer) => _channels[consumer].Reader;

    public ValueTask WriteAsync(VirtualFile file, CancellationToken ct)
    {
        foreach (var channel in _channels.Values)
        {
            // Unbounded: writes complete synchronously, unless the channel was completed by a failure.
            channel.Writer.TryWrite(file);
        }

        return ValueTask.CompletedTask;
    }

    /// <param name="error">Null when the producer succeeded; otherwise consumers see the exception once they drained the channel.</param>
    public void Complete(Exception? error)
    {
        foreach (var channel in _channels.Values)
        {
            channel.Writer.TryComplete(error);
        }
    }
}

/// <summary>
/// Completes the channels of a node that did not succeed, so that its dependents know why their input ended.
/// </summary>
internal sealed class StepUnavailableException : Exception
{
    public StepUnavailableException(string step, StepState state)
        : base($"step '{step}' {state.ToString().ToLowerInvariant()}")
    {
        Step = step;
        State = state;
    }

    public string Step { get; }

    public StepState State { get; }
}
