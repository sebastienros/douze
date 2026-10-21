using System.Diagnostics;
using System.Globalization;
using System.Text;
using Douze.Tracing;

namespace Douze.Workflow.Commands;

/// <summary>
/// The outcome of a command that ran to its end. <see cref="Stdout"/> is empty when it was not captured.
/// </summary>
internal sealed record CommandResult(int ExitCode, ReadOnlyMemory<byte> Stdout, string Stderr, TimeSpan Elapsed);

/// <summary>
/// Starts external processes: through the shell (<c>/bin/sh -c</c>, <c>cmd.exe /d /s /c</c> on Windows) or directly with
/// the line split by <see cref="Split"/>; with redirected streams, a working directory, environment variables, a
/// timeout that kills the whole process tree, and a <c>command</c> trace span.
/// </summary>
internal static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <exception cref="DouzeException">The command line is empty.</exception>
    public static ProcessStartInfo CreateStartInfo(string commandLine, bool shell, string workingDirectory, IEnumerable<KeyValuePair<string, string>> environment)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = workingDirectory,
        };

        if (shell)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                throw new DouzeException("the command line is empty");
            }

            if (OperatingSystem.IsWindows())
            {
                // /s: cmd removes the first and the last quote and runs the rest as typed, so the line keeps its own quotes.
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.Arguments = "/d /s /c \"" + commandLine + "\"";
            }
            else
            {
                info.FileName = "/bin/sh";
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add(commandLine);
            }
        }
        else
        {
            var arguments = Split(commandLine);

            if (arguments.Count == 0)
            {
                throw new DouzeException("the command line is empty");
            }

            info.FileName = arguments[0];

            for (var i = 1; i < arguments.Count; i++)
            {
                info.ArgumentList.Add(arguments[i]);
            }
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    /// <summary>
    /// Splits a command line into arguments, for <c>shell: false</c>: whitespace separates arguments, <c>'...'</c> is
    /// literal, and in <c>"..."</c> a <c>\"</c> is a quote. Backslashes are literal everywhere else, so Windows paths
    /// need no escaping.
    /// </summary>
    /// <exception cref="DouzeException">A quote is not closed.</exception>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (char.IsWhiteSpace(c))
            {
                if (inArgument)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inArgument = false;
                }

                continue;
            }

            inArgument = true;

            if (c is '\'' or '"')
            {
                var quote = c;
                var closed = false;

                for (i++; i < commandLine.Length; i++)
                {
                    c = commandLine[i];

                    if (c == quote)
                    {
                        closed = true;
                        break;
                    }

                    if (quote == '"' && c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        c = '"';
                        i++;
                    }

                    current.Append(c);
                }

                if (!closed)
                {
                    throw new DouzeException($"unclosed {quote} in the command line: {commandLine}");
                }

                continue;
            }

            current.Append(c);
        }

        if (inArgument)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>
    /// Runs a process to its end. <paramref name="writeInput"/> writes stdin, which is then closed (closed at once when
    /// null). stdout is captured when <paramref name="captureOutput"/> is true, and handed to <paramref name="onOutputLine"/>
    /// line by line otherwise (or discarded when it is null). stderr is always captured. <paramref name="what"/> names the
    /// unit in messages: <c>" for '/a.md'"</c>, or an empty string.
    /// </summary>
    /// <exception cref="DouzeException">The process cannot start, or it timed out (it is then killed with its children).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the process was killed with its children.</exception>
    public static async Task<CommandResult> RunAsync(
        ProcessStartInfo info,
        Func<Stream, CancellationToken, ValueTask>? writeInput,
        bool captureOutput,
        Action<string>? onOutputLine,
        TimeSpan? timeout,
        ITraceRecorder trace,
        string step,
        string what,
        CancellationToken ct)
    {
        using var span = trace.Begin(step, "command");
        span.AddArg("command", DisplayCommand(info));
        var stopwatch = Stopwatch.StartNew();

        using var process = Start(info, what);

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        // Killing the tree ends the reads below, and the wait.
        using var kill = token.Register(static state => KillTree((Process)state!), process);

        var stdout = captureOutput
            ? ReadAllAsync(process.StandardOutput.BaseStream)
            : ReadLinesAsync(process.StandardOutput, onOutputLine);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdin = WriteInputAsync(process.StandardInput.BaseStream, writeInput, token);

        int exitCode;

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr, stdin);
            exitCode = process.ExitCode;
        }
        finally
        {
            span.AddArg("elapsed", stopwatch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
        }

        ct.ThrowIfCancellationRequested();

        if (timeoutCts.IsCancellationRequested)
        {
            throw new DouzeException($"command timed out after {FormatDuration(timeout!.Value)}{what}");
        }

        span.AddArg("exitCode", exitCode.ToString(CultureInfo.InvariantCulture));

        return new CommandResult(exitCode, captureOutput ? await stdout : ReadOnlyMemory<byte>.Empty, await stderr, stopwatch.Elapsed);
    }

    /// <exception cref="DouzeException">The process cannot start.</exception>
    public static Process Start(ProcessStartInfo info, string what)
    {
        try
        {
            return Process.Start(info) ?? throw new DouzeException($"cannot start '{info.FileName}'{what}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new DouzeException($"cannot start '{info.FileName}'{what}: {e.Message}");
        }
    }

    /// <summary>
    /// Kills a process and all its descendants. Never throws.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    /// <summary>
    /// The last <paramref name="count"/> non-empty lines of <paramref name="text"/>.
    /// </summary>
    public static string Tail(string text, int count = 20)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count)));
    }

    public static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 1
            ? duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms"
            : duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }

    private static string DisplayCommand(ProcessStartInfo info)
    {
        return info.ArgumentList.Count > 0
            ? info.FileName + " " + string.Join(' ', info.ArgumentList)
            : info.FileName + " " + info.Arguments;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.GetBuffer().AsMemory(0, (int)memory.Length);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadLinesAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine?.Invoke(line);
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    private static async Task WriteInputAsync(Stream stdin, Func<Stream, CancellationToken, ValueTask>? write, CancellationToken ct)
    {
        try
        {
            if (write != null)
            {
                await write(stdin, ct);
            }
        }
        catch (IOException)
        {
            // The process exited or closed stdin without reading everything: its exit code tells what happened.
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                stdin.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }
}
