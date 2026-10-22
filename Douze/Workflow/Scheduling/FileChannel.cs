using System.Threading.Channels;

namespace Douze.Workflow.Scheduling;

/// <summary>
/// The output of a node: one channel per consumer, so that every output reaches every dependent (fan-out). A channel is
/// bounded when its consumer reads as soon as the producer writes (backpressure: a producer waits for the slowest of
/// its bounded consumers), unbounded when it must hold everything, in front of a barrier.
/// </summary>
internal sealed class FileChannel
{
    private static readonly UnboundedChannelOptions UnboundedOptions = new() { SingleReader = true, SingleWriter = false };

    private readonly Dictionary<string, Channel<VirtualFile>> _channels = new(StringComparer.Ordinal);
    private readonly Channel<VirtualFile>[] _all;

    /// <param name="consumers">Each consumer with the capacity of its channel, null for unbounded.</param>
    public FileChannel(IEnumerable<(string Consumer, int? Capacity)> consumers)
    {
        foreach (var (consumer, capacity) in consumers)
        {
            if (!_channels.ContainsKey(consumer))
            {
                _channels[consumer] = capacity is { } bound
                    ? Channel.CreateBounded<VirtualFile>(new BoundedChannelOptions(bound) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait })
                    : Channel.CreateUnbounded<VirtualFile>(UnboundedOptions);
            }
        }

        _all = [.. _channels.Values];
    }

    public ChannelReader<VirtualFile> Subscribe(string consumer) => _channels[consumer].Reader;

    /// <summary>
    /// Writes a file to every consumer, waiting while a bounded channel is full. Every consumer either reads its
    /// channel to the end or, once it stopped early, drains it, so the wait always ends.
    /// </summary>
    public ValueTask WriteAsync(VirtualFile file, CancellationToken ct)
    {
        for (var i = 0; i < _all.Length; i++)
        {
            if (!_all[i].Writer.TryWrite(file))
            {
                return WriteSlowAsync(file, i, ct);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteSlowAsync(VirtualFile file, int start, CancellationToken ct)
    {
        for (var i = start; i < _all.Length; i++)
        {
            var writer = _all[i].Writer;

            // False once the channel is complete, which only the producer does after its last write.
            while (!writer.TryWrite(file) && await writer.WaitToWriteAsync(ct))
            {
            }
        }
    }

    /// <param name="error">Null when the producer succeeded; otherwise consumers see the exception once they drained the channel.</param>
    public void Complete(Exception? error)
    {
        foreach (var channel in _all)
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
