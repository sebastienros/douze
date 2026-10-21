using System.Collections.Concurrent;
using Douze.FileSystems;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: write</c>. Writes each file to the output folder and passes it through, so that later steps can run after
/// it. Two files with the same output path are an error.
/// </summary>
internal sealed class WriteStep : Step, IMapStep
{
    private readonly ConcurrentDictionary<FPath, VirtualFile> _written = new();

    public WriteStep(StepDefinition definition) : base(definition)
    {
        _ = new WithOptions(definition);
    }

    public override StepShape NativeShape => StepShape.Map;

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _written.Clear();
        return default;
    }

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        if (!_written.TryAdd(file.Path, file))
        {
            var other = _written[file.Path];
            var (first, second) = Order(Describe(other), Describe(file));
            throw new DouzeException($"output '{file.Path}' is produced by both {first} and {second}");
        }

        var output = context.Build.Output;
        await output.CreateDirectoryAsync(file.Path.GetDirectory());

        await using (var stream = await output.CreateFileAsync(file.Path))
        {
            await file.Content.CopyToAsync(stream, ct);
        }

        return FileList.Of(file);
    }

    private static string Describe(VirtualFile file)
    {
        return file.SourcePath is { } source ? $"'{source}'" : $"step '{file.Provenance.Step}'";
    }

    private static (string, string) Order(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
