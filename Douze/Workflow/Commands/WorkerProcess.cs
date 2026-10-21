using System.Diagnostics;
using Douze.Progress;

namespace Douze.Workflow.Commands;

/// <summary>
/// A long-running worker process speaking the <see cref="WorkerProtocol"/>: started with a handshake, sent requests
/// (at most <see cref="Concurrency"/> at once, answered in any order), and shut down at the end.
/// </summary>
/// <remarks>
/// Any failure of the worker (it exits or closes its output before <c>shutdown</c>, writes a line that is not JSON,
/// writes to stderr with <c>stderr: fail</c>, or a request times out) fails every request in flight and every later
/// one with the same message, and kills the process tree. A failure that no request reported is reported by
/// <see cref="ShutdownAsync"/>.
/// </remarks>
internal sealed class WorkerProcess
{
    /// <summary>
    /// How long the worker has to answer <c>hello</c>. Settable for tests.
    /// </summary>
    internal static TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the worker has to exit after <c>shutdown</c> before it is killed. Settable for tests.
    /// </summary>
    internal static TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(10);

    // How long stderr lines are collected before 'stderr: fail' fails the worker, so the message has all of them.
    private static readonly TimeSpan StderrSettleDelay = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly StderrPolicy _stderrPolicy;
    private readonly Action<LogLevel, string> _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<WorkerMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<ReadyMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stdoutLoop;
    private readonly Task _stderrLoop;
    private SemaphoreSlim? _slots;
    private string? _failure;
    private bool _failureReported;
    private bool _stderrPending;
    private volatile bool _shuttingDown;

    private WorkerProcess(Process process, StderrPolicy stderrPolicy, Action<LogLevel, string> log)
    {
        _process = process;
        _stderrPolicy = stderrPolicy;
        _log = log;
        _stdoutLoop = Task.Run(ReadOutputAsync);
        _stderrLoop = Task.Run(ReadErrorAsync);
    }

    /// <summary>
    /// The number of requests sent at once: what the worker announced in <c>ready</c>, at most what Douze offered.
    /// </summary>
    public int Concurrency { get; private set; }

    public string? Name { get; private set; }

    /// <summary>
    /// Starts the process, sends <paramref name="hello"/>, and waits for <c>ready</c>.
    /// </summary>
    /// <exception cref="DouzeException">The process cannot start, or the handshake fails; the process is then killed.</exception>
    public static async Task<WorkerProcess> StartAsync(ProcessStartInfo info, ReadOnlyMemory<byte> hello, int maxConcurrency, StderrPolicy stderrPolicy, Action<LogLevel, string> log, CancellationToken ct)
    {
        var worker = new WorkerProcess(ProcessRunner.Start(info, ""), stderrPolicy, log);

        try
        {
            await worker.WriteAsync(hello);

            ReadyMessage ready;

            try
            {
                ready = await worker._ready.Task.WaitAsync(HandshakeTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw worker.Fail($"worker did not answer 'hello' within {ProcessRunner.FormatDuration(HandshakeTimeout)}");
            }

            if (ready.Protocol != WorkerProtocol.Version)
            {
                throw worker.Fail($"worker speaks protocol {ready.Protocol}, Douze speaks protocol {WorkerProtocol.Version}");
            }

            worker.Name = ready.Name;
            worker.Concurrency = Math.Clamp(ready.Concurrency, 1, maxConcurrency);
            worker._slots = new SemaphoreSlim(worker.Concurrency, worker.Concurrency);
            return worker;
        }
        catch
        {
            lock (worker._lock)
            {
                // The caller reports this exception: no need to report the failure again at shutdown.
                worker._failureReported = true;
            }

            ProcessRunner.KillTree(worker._process);
            await worker.WaitForLoopsAsync();
            worker._process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a request and waits for its <c>result</c> or <c>error</c>. <paramref name="what"/> names the unit in messages.
    /// </summary>
    /// <exception cref="DouzeException">The worker failed, or the request timed out (the worker is then killed).</exception>
    public async Task<WorkerMessage> RequestAsync(string id, ReadOnlyMemory<byte> message, TimeSpan? timeout, string what, CancellationToken ct)
    {
        await _slots!.WaitAsync(ct);

        try
        {
            var response = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_lock)
            {
                ThrowIfFailed();
                _pending[id] = response;
            }

            await WriteAsync(message);

            using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await response.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Fail($"worker timed out after {ProcessRunner.FormatDuration(timeout!.Value)}{what}");
                throw Reported();
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    _pending.Remove(id);
                }

                throw;
            }
            catch (DouzeException)
            {
                throw Reported();
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Sends <c>shutdown</c>, closes stdin, and waits for the worker to exit; kills it after <see cref="ShutdownGrace"/>.
    /// </summary>
    /// <exception cref="DouzeException">The worker exited with a non-zero code, or failed without any request reporting it.</exception>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        var killed = false;

        if (!_process.HasExited)
        {
            try
            {
                await WriteAsync(WorkerProtocol.Shutdown());
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It exited meanwhile.
            }

            using var grace = new CancellationTokenSource(ShutdownGrace);

            try
            {
                await _process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                ProcessRunner.KillTree(_process);
                killed = true;
                _log(LogLevel.Warning, $"worker did not exit within {ProcessRunner.FormatDuration(ShutdownGrace)} after 'shutdown' and was killed");
                await _process.WaitForExitAsync();
            }
        }

        await WaitForLoopsAsync();

        var exitCode = _process.ExitCode;
        _process.Dispose();

        if (_stderrPending)
        {
            Fail($"worker wrote to stderr: {StderrSuffix()[2..]}");
        }

        lock (_lock)
        {
            if (_failure != null)
            {
                if (!_failureReported)
                {
                    _failureReported = true;
                    throw new DouzeException(_failure);
                }

                return;
            }
        }

        if (exitCode != 0 && !killed)
        {
            throw new DouzeException($"worker exited with code {exitCode} after 'shutdown'{StderrSuffix()}");
        }
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> message)
    {
        await _writeLock.WaitAsync();

        try
        {
            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(message);
            await stdin.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The worker exited: wait for the output loop to say why.
            await _stdoutLoop.WaitAsync(ShutdownGrace).ContinueWith(static _ => { }, TaskScheduler.Default);
            lock (_lock)
            {
                ThrowIfFailed();
            }

            throw new DouzeException("worker closed its input");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                WorkerMessage message;

                try
                {
                    message = WorkerProtocol.Parse(line);
                }
                catch (DouzeException e)
                {
                    Fail(e.Message);
                    return;
                }

                switch (message)
                {
                    case ReadyMessage ready:
                        _ready.TrySetResult(ready);
                        break;

                    case ResultMessage or ErrorMessage:
                        var id = message is ResultMessage result ? result.Id : ((ErrorMessage)message).Id;
                        TaskCompletionSource<WorkerMessage>? response;

                        lock (_lock)
                        {
                            // A request that was cancelled meanwhile is not pending anymore: its answer is dropped.
                            _pending.Remove(id, out response);
                        }

                        response?.TrySetResult(message);
                        break;

                    case LogMessage log:
                        _log(log.Level.ToLowerInvariant() switch
                        {
                            "debug" => LogLevel.Debug,
                            "warn" or "warning" => LogLevel.Warning,
                            "error" => LogLevel.Error,
                            _ => LogLevel.Info,
                        }, log.Message);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        if (_shuttingDown)
        {
            return;
        }

        // Closed before 'shutdown': the worker exited, or closed its stdout and is killed.
        await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(ShutdownGrace));

        if (!_process.HasExited)
        {
            Fail("worker closed its output before 'shutdown'");
            return;
        }

        // stderr is read to its end first, so the message has all of it.
        await _stderrLoop.WaitAsync(ShutdownGrace).ContinueWith(static _ => { }, TaskScheduler.Default);
        Fail($"worker exited with code {_process.ExitCode}{StderrSuffix()}");
    }

    private async Task ReadErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                lock (_lock)
                {
                    _stderrTail.Enqueue(line);

                    if (_stderrTail.Count > 20)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                switch (_stderrPolicy)
                {
                    case StderrPolicy.Warn:
                        _log(LogLevel.Warning, $"worker wrote to stderr: {line}");
                        break;

                    case StderrPolicy.Fail when !_stderrPending:
                        _stderrPending = true;
                        _ = Task.Delay(StderrSettleDelay).ContinueWith(_ => FailWithStderr(), TaskScheduler.Default);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        // At the end of stderr the worker exited: the output loop reports the exit code with stderr, or the shutdown
        // reports stderr.
    }

    private void FailWithStderr()
    {
        // A shutdown, or an exit (reported by the output loop), says it with stderr.
        if (_shuttingDown || _process.HasExited)
        {
            return;
        }

        string tail;

        lock (_lock)
        {
            tail = string.Join(Environment.NewLine, _stderrTail);
        }

        Fail($"worker wrote to stderr: {tail}");
    }

    /// <summary>
    /// Records the first failure, fails every pending request and the handshake, and kills the process tree.
    /// Returns the exception to throw for the caller's own request.
    /// </summary>
    private DouzeException Fail(string message)
    {
        List<TaskCompletionSource<WorkerMessage>> pending;

        lock (_lock)
        {
            if (_failure != null)
            {
                return new DouzeException(_failure);
            }

            _failure = message;
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var response in pending)
        {
            response.TrySetException(new DouzeException(message));
        }

        _ready.TrySetException(new DouzeException(message));

        if (!_shuttingDown)
        {
            ProcessRunner.KillTree(_process);
        }

        return new DouzeException(message);
    }

    /// <summary>
    /// The exception a request throws for the worker's failure, which then counts as reported.
    /// </summary>
    private DouzeException Reported()
    {
        lock (_lock)
        {
            _failureReported = true;
            return new DouzeException(_failure!);
        }
    }

    // Called under _lock.
    private void ThrowIfFailed()
    {
        if (_failure != null)
        {
            _failureReported = true;
            throw new DouzeException(_failure);
        }
    }

    private string StderrSuffix()
    {
        lock (_lock)
        {
            return _stderrTail.Count > 0 ? ": " + string.Join(Environment.NewLine, _stderrTail) : "";
        }
    }

    private async Task WaitForLoopsAsync()
    {
        // A grandchild that keeps the pipes open must not block the build.
        await Task.WhenAll(_stdoutLoop, _stderrLoop).WaitAsync(ShutdownGrace).ContinueWith(static _ => { }, TaskScheduler.Default);
    }
}
