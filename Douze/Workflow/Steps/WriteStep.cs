using System.Collections.Concurrent;
using Douze.FileSystems;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: write</c>. Writes each file to the output folder and passes it through, so that later steps can run after
/// it. Two files with the same output path are an error, unless they are the same file (path, content and metadata): an
/// image variant used by two pages is produced by both, and written once. A file whose unit is replayed is not written
/// again: it is in the output folder as written. What a previous build wrote and this one does not is deleted once the
/// build succeeded (<see cref="StaleOutputs"/>). A written file is passed on with its content read from the output
/// folder (<see cref="FileContent.FromWritten"/>), not kept in memory: a step after <c>write</c> (a search indexer)
/// receives every file of the site, and would otherwise hold every content until <c>write</c> is done.
/// </summary>
internal sealed class WriteStep : Step, IMapStep, IReplayObserver
{
    // Output paths, what produced them and when they are written: not the files, whose contents would stay in memory
    // until the build ends.
    private readonly ConcurrentDictionary<FPath, Registration> _written = new();

    private sealed record Registration(string Producer, FileRef Ref, Task Written);

    public WriteStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override bool ReadsScopes => false;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _written.Clear();
        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = context.Build.Output;
        long? length = null;

        if (Register(file, writing.Task) is { } written)
        {
            // The same file, written by another unit.
            await written.WaitAsync(ct);
        }
        else if (file.Content.IsReadFrom(output, file.Path))
        {
            // Written there by a write step before this one: writing it again would truncate what it reads.
            writing.SetResult();
        }
        else
        {
            try
            {
                // Creates the directory too.
                using (var bytes = await file.Content.RentBytesAsync(ct))
                {
                    await output.WriteFileAsync(file.Path, bytes.Memory);
                    length = bytes.Memory.Length;
                }

                writing.SetResult();
            }
            catch (Exception e)
            {
                writing.SetException(e);
                throw;
            }
        }

        // The result is reused while the file is in the output folder as written, and the next build deletes it if it
        // does not write it again (StaleOutputs). Without the cache, a one-shot build has no next build to tell.
        if (context.Build.Cache != null || context.Build.Options.RecordWritten)
        {
            var info = await context.Build.Output.GetFileMetadataAsync(file.Path);
            context.Dependencies.RecordWritten(file.Path, info.Length, info.LastWriteTimeUtc);
        }

        if (file.Content.IsReadFrom(output, file.Path))
        {
            return FileList.Of(file);
        }

        length ??= file.Content.Length ?? (await output.GetFileMetadataAsync(file.Path)).Length;
        return FileList.Of(file.With(Name, content: FileContent.FromWritten(output, file.Path, length.Value, file.Content)));
    }

    /// <summary>
    /// A file whose unit is replayed is not written again, but it is in the output all the same: two files with its path
    /// are an error, as when both are written.
    /// </summary>
    public void Replayed(IReadOnlyList<VirtualFile> inputs, IReadOnlyList<VirtualFile> outputs)
    {
        foreach (var file in inputs)
        {
            Register(file, Task.CompletedTask);
        }
    }

    /// <summary>
    /// Registers the output path of <paramref name="file"/>, written once <paramref name="written"/> completes. Returns
    /// null when the path is new, or when the same file (same <see cref="FileRef"/>) was registered first, the task
    /// that completes once that one is written.
    /// </summary>
    /// <exception cref="DouzeException">Another file has the path.</exception>
    private Task? Register(VirtualFile file, Task written)
    {
        var registration = new Registration(Describe(file), file.Ref, written);
        var existing = _written.GetOrAdd(file.Path, registration);

        if (ReferenceEquals(existing, registration))
        {
            return null;
        }

        if (existing.Ref == registration.Ref)
        {
            return existing.Written;
        }

        var (first, second) = Order(existing.Producer, registration.Producer);
        throw new DouzeException($"output '{file.Path}' is produced by both {first} and {second}");
    }

    private static string Describe(VirtualFile file)
    {
        return file.SourcePath is { } source ? $"'{source}'" : $"step '{file.Provenance.Step}'";
    }

    private static (string, string) Order(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
