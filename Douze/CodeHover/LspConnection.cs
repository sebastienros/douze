using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Douze.Workflow.Commands;

namespace Douze.CodeHover;

/// <summary>
/// A language server process spoken to with JSON-RPC over its standard input and output (the Language Server
/// Protocol's base protocol: <c>Content-Length</c> headers). Requests are pipelined: any number can be in flight, and
/// are answered in any order. Requests from the server (configuration, registrations) are answered with
/// <c>null</c>; its notifications are ignored, but for the diagnostics it pushes, kept in
/// <see cref="PushedDiagnostics"/>, and those that <see cref="WaitForNotificationAsync"/> waits for.
/// </summary>
/// <remarks>
/// When the process exits or writes something that is not the protocol, every pending request and every later one
/// fails with the same <see cref="DouzeException"/>.
/// </remarks>
internal sealed class LspConnection
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Process _process;
    private readonly string _name;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _notifications = new(StringComparer.Ordinal);
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _readLoop;
    private readonly Task _stderrLoop;
    private long _nextId;
    private string? _failure;
    private volatile bool _closing;

    private LspConnection(Process process, string name)
    {
        _process = process;
        _name = name;
        _input = process.StandardInput.BaseStream;
        _readLoop = Task.Run(ReadLoopAsync);
        _stderrLoop = Task.Run(ReadErrorAsync);
    }

    /// <summary>
    /// The diagnostics the server published, by document URI (the last publication of each).
    /// </summary>
    public Dictionary<string, JsonElement> PushedDiagnostics { get; } = new(StringComparer.Ordinal);

    /// <exception cref="DouzeException">The process cannot start.</exception>
    public static LspConnection Start(ProcessStartInfo info, string name)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        return new LspConnection(ProcessRunner.Start(info, ""), name);
    }

    /// <summary>
    /// Sends a request whose parameters <paramref name="writeParams"/> writes (an object or an array), and returns its
    /// result.
    /// </summary>
    /// <exception cref="DouzeException">The server answered with an error, or failed.</exception>
    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct)
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;

        lock (_lock)
        {
            ThrowIfFailed();
            id = ++_nextId;
            _pending[id] = response;
        }

        await WriteAsync(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writeParams(writer);
        });

        using var registration = ct.Register(() => response.TrySetCanceled(ct));
        return await response.Task;
    }

    /// <summary>
    /// Returns the parameters of the first notification <paramref name="method"/> of the server, which may have arrived
    /// already.
    /// </summary>
    /// <exception cref="DouzeException">The server failed first.</exception>
    public async Task<JsonElement> WaitForNotificationAsync(string method, CancellationToken ct)
    {
        TaskCompletionSource<JsonElement> notification;

        lock (_lock)
        {
            notification = Notification(method);

            if (!notification.Task.IsCompleted)
            {
                ThrowIfFailed();
            }
        }

        return await notification.Task.WaitAsync(ct);
    }

    // Under _lock.
    private TaskCompletionSource<JsonElement> Notification(string method)
    {
        if (!_notifications.TryGetValue(method, out var notification))
        {
            _notifications[method] = notification = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return notification;
    }

    public Task NotifyAsync(string method, Action<Utf8JsonWriter>? writeParams) => WriteAsync(writer =>
    {
        writer.WriteString("method", method);

        if (writeParams != null)
        {
            writer.WritePropertyName("params");
            writeParams(writer);
        }
    });

    /// <summary>
    /// Sends <c>shutdown</c> and <c>exit</c>, and waits for the process to exit; kills it after
    /// <paramref name="grace"/>. Never throws.
    /// </summary>
    public async Task CloseAsync(TimeSpan grace)
    {
        try
        {
            if (!_process.HasExited && _failure == null)
            {
                using var timeout = new CancellationTokenSource(grace);
                await RequestAsync("shutdown", static w => w.WriteNullValue(), timeout.Token);
                _closing = true;
                await NotifyAsync("exit", null);
                _input.Close();
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception e) when (e is DouzeException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Killed below.
        }

        _closing = true;

        if (!_process.HasExited)
        {
            ProcessRunner.KillTree(_process);
        }

        await Task.WhenAll(_readLoop, _stderrLoop).WaitAsync(grace).ContinueWith(static _ => { }, TaskScheduler.Default);
        _process.Dispose();
    }

    /// <summary>
    /// Kills the process now. Never throws.
    /// </summary>
    public void Kill()
    {
        _closing = true;
        ProcessRunner.KillTree(_process);
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> writeBody)
    {
        var body = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.WrittenCount}\r\n\r\n");

        await _writeLock.WaitAsync();

        try
        {
            await _input.WriteAsync(header);
            await _input.WriteAsync(body.WrittenMemory);
            await _input.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The process exited: the read loop says why.
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(static _ => { }, TaskScheduler.Default);

            lock (_lock)
            {
                ThrowIfFailed();
            }

            throw new DouzeException($"{_name} closed its input");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _process.StandardOutput.BaseStream;
        var buffer = new byte[1 << 16];
        var start = 0;
        var end = 0;

        try
        {
            var ended = false;

            while (!ended)
            {
                // The headers end with an empty line.
                int headerEnd;

                while ((headerEnd = buffer.AsSpan(start, end - start).IndexOf("\r\n\r\n"u8)) < 0 && !ended)
                {
                    ended = !await FillAsync();
                }

                if (ended)
                {
                    break;
                }

                var length = ContentLength(buffer.AsSpan(start, headerEnd));
                start += headerEnd + 4;

                if (end - start < length && buffer.Length - start < length)
                {
                    // Move what is read to the front, in a larger buffer if the message needs it.
                    var larger = buffer.Length < length ? new byte[Math.Max(length, buffer.Length * 2)] : buffer;
                    buffer.AsSpan(start, end - start).CopyTo(larger);
                    end -= start;
                    start = 0;
                    buffer = larger;
                }

                while (end - start < length && !ended)
                {
                    ended = !await FillAsync();
                }

                if (ended)
                {
                    break;
                }

                await DispatchAsync(buffer.AsMemory(start, length));
                start += length;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException)
        {
            Fail($"{_name} wrote something that is not the language server protocol: {e.Message}");
            ProcessRunner.KillTree(_process);
            return;
        }

        if (_closing)
        {
            return;
        }

        await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        await _stderrLoop.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(static _ => { }, TaskScheduler.Default);
        Fail(_process.HasExited
            ? $"{_name} exited with code {_process.ExitCode}{StderrSuffix()}"
            : $"{_name} closed its output{StderrSuffix()}");

        // Reads more bytes after what the buffer holds, moving them to its front when it is full.
        async Task<bool> FillAsync()
        {
            if (end == buffer.Length)
            {
                if (start == 0)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
                else
                {
                    buffer.AsSpan(start, end - start).CopyTo(buffer);
                    end -= start;
                    start = 0;
                }
            }

            var read = await stream.ReadAsync(buffer.AsMemory(end));
            end += read;
            return read > 0;
        }
    }

    /// <summary>
    /// The <c>Content-Length</c> of a message's headers.
    /// </summary>
    private static int ContentLength(ReadOnlySpan<byte> headers)
    {
        foreach (var range in headers.Split("\r\n"u8))
        {
            var line = Encoding.ASCII.GetString(headers[range]);
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidDataException("a message has no Content-Length header");
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var hasMethod = root.TryGetProperty("method", out var method);
        var hasId = root.TryGetProperty("id", out var id);

        if (hasMethod && hasId)
        {
            // A request of the server: configuration sections and registrations get null.
            var count = method.GetString() == "workspace/configuration"
                && root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("items", out var items) ? items.GetArrayLength() : -1;
            var rawId = id.GetRawText();

            await WriteAsync(writer =>
            {
                writer.WritePropertyName("id");
                writer.WriteRawValue(rawId);
                writer.WritePropertyName("result");

                if (count < 0)
                {
                    writer.WriteNullValue();
                    return;
                }

                writer.WriteStartArray();

                for (var i = 0; i < count; i++)
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndArray();
            });

            return;
        }

        if (hasMethod)
        {
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            lock (_lock)
            {
                Notification(method.GetString() ?? "").TrySetResult(parameters);
            }

            if (method.GetString() == "textDocument/publishDiagnostics"
                && root.TryGetProperty("params", out var published)
                && published.TryGetProperty("uri", out var uri))
            {
                lock (_lock)
                {
                    PushedDiagnostics[uri.GetString() ?? ""] = published.Clone();
                }
            }

            return;
        }

        if (!hasId || id.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        TaskCompletionSource<JsonElement>? response;

        lock (_lock)
        {
            _pending.Remove(id.GetInt64(), out response);
        }

        if (response == null)
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
            response.TrySetException(new DouzeException($"{_name}: {message}"));
            return;
        }

        response.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
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
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private string StderrSuffix()
    {
        lock (_lock)
        {
            return _stderrTail.Count == 0 ? "" : ": " + string.Join(Environment.NewLine, _stderrTail);
        }
    }

    private void Fail(string message)
    {
        List<TaskCompletionSource<JsonElement>> pending;

        lock (_lock)
        {
            _failure ??= message;
            pending = [.. _pending.Values, .. _notifications.Values];
            _pending.Clear();
        }

        foreach (var response in pending)
        {
            response.TrySetException(new DouzeException(message));
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure != null)
        {
            throw new DouzeException(_failure);
        }
    }
}
