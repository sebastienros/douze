using System.Collections.Concurrent;
using Douze.FileSystems;

namespace Douze.Workflow.Steps;

/// <summary>
/// <c>uses: write</c>. Writes each file to the output folder and passes it through, so that later steps can run after
/// it. Two files with the same output path are an error.
/// </summary>
internal sealed class WriteStep : Step, IMapStep
{
    // Output paths and what produced them: not the files, whose contents would stay in memory until the build ends.
    private readonly ConcurrentDictionary<FPath, string> _written = new();

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
        if (!_written.TryAdd(file.Path, Describe(file)))
        {
            var (first, second) = Order(_written[file.Path], Describe(file));
            throw new DouzeException($"output '{file.Path}' is produced by both {first} and {second}");
        }

        // Creates the directory too.
        await using (var stream = await context.Build.Output.CreateFileAsync(file.Path))
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
