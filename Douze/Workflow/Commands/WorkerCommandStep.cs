using System.Globalization;
using Douze.FileSystems;
using Douze.Progress;

namespace Douze.Workflow.Commands;

/// <summary>
/// <c>mode: worker</c>: one long-running process for the step, sent each consumed file over the
/// <see cref="WorkerProtocol"/>. The process starts when the step starts, so its startup overlaps with the steps
/// before it, and is shut down when the step is done.
/// </summary>
/// <remarks>
/// A <c>result</c> file without <c>path</c> keeps the input path, without <c>content</c> the input content, and its
/// <c>metadata</c> is merged over the input's own metadata (top-level keys). Without <c>contentType</c>, the content
/// type is the input's when the path is unchanged, and derived from the new path otherwise. <c>timeout</c> applies to
/// each request.
/// </remarks>
internal sealed class WorkerCommandStep : CommandStep, IMapStep
{
    private Task<WorkerProcess>? _worker;
    private long _nextId;
    private volatile bool _startReported;

    public WorkerCommandStep(StepDefinition definition) : base(definition)
    {
    }

    public override StepShape NativeShape => StepShape.Map;

    public override async ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        await base.InitializeAsync(context, ct);

        var info = await CreateStartInfoAsync(CreateModel(), [], ct);
        var hello = WorkerProtocol.Hello(Name, context.MaxDegreeOfParallelism, Definition.With, context.Site.OutputDirectory, context.Site.SourceDirectory);

        // Not awaited: the first unit waits for the handshake. The build's token, not the step's, so that the units
        // of a step stopped by a failure see the same worker until it shuts down.
        _worker = StartAsync(info, hello, context.MaxDegreeOfParallelism, context.Cancellation);
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        WorkerProcess worker;

        try
        {
            worker = await _worker!.WaitAsync(ct);
        }
        catch (DouzeException)
        {
            _startReported = true;
            throw;
        }

        var what = $" for '{file.Path}'";
        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var request = await WorkerProtocol.FileAsync(id, file, ct);

        var response = await worker.RequestAsync(id, request, Definition.Timeout, what, ct);

        if (response is ErrorMessage error)
        {
            throw new DouzeException(Describe(error, file));
        }

        var files = ((ResultMessage)response).Files;

        if (files.Count == 0)
        {
            return FileList.None;
        }

        var outputs = new VirtualFile[files.Count];

        for (var i = 0; i < files.Count; i++)
        {
            outputs[i] = ToOutput(file, files[i]);
        }

        return FileList.Of(outputs);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_worker != null)
            {
                WorkerProcess worker;

                try
                {
                    worker = await _worker;
                }
                catch (DouzeException) when (_startReported)
                {
                    // The units reported why the worker did not start.
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await worker.ShutdownAsync();
            }
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    private async Task<WorkerProcess> StartAsync(System.Diagnostics.ProcessStartInfo info, ReadOnlyMemory<byte> hello, int concurrency, CancellationToken ct)
    {
        using var span = Build.Trace.Begin(Name, "command");
        span.AddArg("worker", "start");

        var worker = await WorkerProcess.StartAsync(info, hello, concurrency, Definition.Stderr, (level, message) => Build.View.Message(level, Name, message), ct);

        Build.View.Message(LogLevel.Debug, Name, $"worker{(worker.Name is { } name ? $" '{name}'" : "")} ready, {worker.Concurrency} request(s) at once");
        return worker;
    }

    private VirtualFile ToOutput(VirtualFile input, WorkerFile output)
    {
        var path = output.Path is { } p ? new FPath(p).ToAbsolute() : input.Path;

        if (!path.IsFile)
        {
            throw new DouzeException($"worker returned '{output.Path}', which is not a file path, for '{input.Path}'");
        }

        var metadata = output.Metadata is { } values
            ? input.Metadata.With(own =>
            {
                foreach (var property in values)
                {
                    own[property.Key] = property.Value;
                }
            })
            : null;

        // A new path without a content type gets the type of its extension, not the input's.
        var contentType = output.ContentType ?? (path != input.Path && input.ContentType != null ? ContentTypes.FromPath(path) : null);

        return input.With(Name, path, output.Content, metadata, contentType);
    }

    private static string Describe(ErrorMessage error, VirtualFile file)
    {
        var message = error.Message;

        if (error.File != null && error.File != file.Path.FullName)
        {
            message += $" in '{error.File}'";
        }

        if (error.Line is { } line)
        {
            message += error.Column is { } column ? $" (line {line}, column {column})" : $" (line {line})";
        }

        return message;
    }
}
