namespace Douze.Workflow.Commands;

/// <summary>
/// <c>mode: filter</c>: one process per consumed file. The content goes to stdin, stdout becomes the new content, the
/// path and the metadata are unchanged.
/// </summary>
/// <remarks>
/// Environment: <c>DOUZE_PATH</c> (the virtual path), <c>DOUZE_CONTENT_TYPE</c>, and <c>DOUZE_METADATA</c> (the file's own
/// metadata as JSON), or <c>DOUZE_METADATA_FILE</c> (a temporary file holding it) when the JSON is longer than
/// <see cref="MaxMetadataLength"/> characters, the size Windows allows for one variable.
/// </remarks>
internal sealed class FilterCommandStep : CommandStep, IMapStep
{
    public const int MaxMetadataLength = 32 * 1024 - 1;

    public FilterCommandStep(StepDefinition definition) : base(definition)
    {
    }

    public override StepShape NativeShape => StepShape.Map;

    public async ValueTask<FileList> ProcessAsync(VirtualFile file, StepContext context, CancellationToken ct)
    {
        var what = $" for '{file.Path}'";
        var metadata = MetadataJson(file.Metadata);
        string? metadataFile = null;

        var environment = new List<KeyValuePair<string, string>>
        {
            new("DOUZE_PATH", file.Path.FullName),
            new("DOUZE_CONTENT_TYPE", file.EffectiveContentType),
        };

        if (metadata.Length <= MaxMetadataLength)
        {
            environment.Add(new("DOUZE_METADATA", metadata));
        }
        else
        {
            Directory.CreateDirectory(TempFolder);
            metadataFile = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(metadataFile, metadata, ct);
            environment.Add(new("DOUZE_METADATA_FILE", metadataFile));
        }

        try
        {
            var info = await CreateStartInfoAsync(CreateModel(file: file.Path), environment, ct);

            var result = await ProcessRunner.RunAsync(
                info,
                (stdin, ct) => file.Content.CopyToAsync(stdin, ct),
                captureOutput: true,
                onOutputLine: null,
                Definition.Timeout,
                Build.Trace,
                Name,
                what,
                ct);

            Check(result, what, context);

            return FileList.Of(file.With(Name, content: FileContent.FromBytes(result.Stdout, file.Content.IsText)));
        }
        finally
        {
            if (metadataFile != null)
            {
                File.Delete(metadataFile);
            }
        }
    }
}
