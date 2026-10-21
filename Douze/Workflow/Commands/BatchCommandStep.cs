using System.Text.Json;
using System.Text.Json.Nodes;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Commands;

/// <summary>
/// <c>mode: batch</c>: one process for the whole input (reduce), or one per group with <c>group-by</c>.
/// </summary>
/// <remarks>
/// When the command references <c>{{ inputs }}</c>, the consumed files are written under
/// <c>&lt;temp&gt;/&lt;step&gt;/&lt;unit&gt;/in/</c> with a <c>douze-inputs.json</c> manifest. When it references
/// <c>{{ outputs }}</c>, <c>&lt;...&gt;/out</c> is created empty and the files the command writes there (those matching
/// <c>outputs</c>) are read back, with the metadata of an optional <c>douze-outputs.json</c>. Otherwise nothing is written
/// to disk and the step emits no files. stdout is forwarded as debug messages. The unit folder is deleted after the run,
/// unless <c>--verbose</c>.
/// </remarks>
internal sealed class BatchCommandStep : CommandStep, IReduceStep, IGroupStep
{
    public const string InputsManifest = "douze-inputs.json";
    public const string OutputsManifest = "douze-outputs.json";

    private const string ReduceUnit = "*";

    private readonly GlobSet _outputs;

    public BatchCommandStep(StepDefinition definition) : base(definition)
    {
        _outputs = definition.Outputs.IsEmpty ? GlobSet.All : GlobSet.Parse(definition.Outputs);
    }

    public override StepShape NativeShape => StepShape.Reduce;

    public override bool SupportsGrouping => true;

    public async ValueTask<StepOutput> ProcessAllAsync(IReadOnlyList<VirtualFile> files, StepContext context, CancellationToken ct)
    {
        return new StepOutput(await RunAsync(files, ReduceUnit, "all", "", context, ct));
    }

    public async ValueTask<StepOutput> ProcessGroupAsync(FileGroup group, StepContext context, CancellationToken ct)
    {
        // Group keys can be anything (paths, rendered templates): the folder is named after their hash.
        var folder = Hash128.OfText(group.Key).ToString()[..16];
        return new StepOutput(await RunAsync(group.Files, group.Key, folder, $" for group '{group.Key}'", context, ct));
    }

    private async ValueTask<FileList> RunAsync(IReadOnlyList<VirtualFile> files, string unit, string folderName, string what, StepContext context, CancellationToken ct)
    {
        if (!Template.UsesInputs && !Template.UsesOutputs)
        {
            await ExecuteAsync(CreateModel(), what, context, ct);
            return FileList.None;
        }

        var folder = Path.Combine(TempFolder, folderName);
        var inputs = Path.Combine(folder, "in");
        var outputs = Path.Combine(folder, "out");

        // A folder left by an earlier build with --verbose cannot be reused: the build id is in the temp root.
        DeleteDirectory(folder);

        try
        {
            if (Template.UsesInputs)
            {
                await WriteInputsAsync(files, unit, inputs, ct);
            }

            if (Template.UsesOutputs)
            {
                Directory.CreateDirectory(outputs);
            }

            await ExecuteAsync(CreateModel(
                Template.UsesInputs ? inputs : null,
                Template.UsesOutputs ? outputs : null), what, context, ct);

            return Template.UsesOutputs ? await ReadOutputsAsync(files, outputs, ct) : FileList.None;
        }
        finally
        {
            if (!Build.Options.Verbose)
            {
                DeleteDirectory(folder);
            }
        }
    }

    private async ValueTask ExecuteAsync(CommandModel model, string what, StepContext context, CancellationToken ct)
    {
        var info = await CreateStartInfoAsync(model, [], ct);

        var result = await ProcessRunner.RunAsync(
            info,
            writeInput: null,
            captureOutput: false,
            onOutputLine: line => context.Log(LogLevel.Debug, line),
            Definition.Timeout,
            Build.Trace,
            Name,
            what,
            ct);

        Check(result, what, context);
    }

    /// <summary>
    /// Writes every file under <paramref name="folder"/> at its virtual path, and the manifest.
    /// </summary>
    private async ValueTask WriteInputsAsync(IReadOnlyList<VirtualFile> files, string unit, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        foreach (var file in files)
        {
            var path = ToDiskPath(folder, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var stream = File.Create(path);
            await file.Content.CopyToAsync(stream, ct);
        }

        await using var manifest = File.Create(Path.Combine(folder, InputsManifest));
        await using var writer = new Utf8JsonWriter(manifest, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("step", Name);
        writer.WriteString("unit", unit);
        writer.WriteStartArray("files");

        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path.FullName);
            writer.WriteString("contentType", file.EffectiveContentType);
            writer.WritePropertyName("metadata");
            CanonicalJson.Write(writer, file.Metadata.Own);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads the files of <paramref name="folder"/> that match <c>outputs</c>, sorted by path.
    /// </summary>
    private async ValueTask<FileList> ReadOutputsAsync(IReadOnlyList<VirtualFile> inputs, string folder, CancellationToken ct)
    {
        var manifest = await ReadOutputsManifestAsync(folder, ct);
        var paths = new List<FPath>();

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var path = new FPath("/" + Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'));

            if (path != "/" + OutputsManifest && _outputs.IsMatch(path))
            {
                paths.Add(path);
            }
        }

        foreach (var path in manifest.Keys)
        {
            if (!File.Exists(ToDiskPath(folder, path)))
            {
                throw new DouzeException($"{OutputsManifest} lists '{path}', which is not in the outputs folder");
            }
        }

        paths.Sort();

        var result = new VirtualFile[paths.Count];

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var bytes = await File.ReadAllBytesAsync(ToDiskPath(folder, path), ct);
            manifest.TryGetValue(path, out var entry);

            result[i] = VirtualFile.Derived(
                Name,
                inputs,
                path,
                FileContent.FromBytes(bytes, FileContent.IsTextPath(path)),
                new Metadata(entry.Metadata ?? new NodeObject(), path.GetDirectory()),
                entry.ContentType);
        }

        return FileList.Of(result);
    }

    private static async ValueTask<Dictionary<FPath, (string? ContentType, NodeObject? Metadata)>> ReadOutputsManifestAsync(string folder, CancellationToken ct)
    {
        var result = new Dictionary<FPath, (string?, NodeObject?)>();
        var path = Path.Combine(folder, OutputsManifest);

        if (!File.Exists(path))
        {
            return result;
        }

        JsonNode? json;

        try
        {
            await using var stream = File.OpenRead(path);
            json = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException e)
        {
            throw new DouzeException($"{OutputsManifest} is not valid JSON: {e.Message}");
        }

        if (json?["files"] is not JsonArray files)
        {
            throw new DouzeException($"{OutputsManifest} needs a 'files' array");
        }

        foreach (var entry in files)
        {
            if (entry?["path"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new DouzeException($"every entry of {OutputsManifest} needs a 'path'");
            }

            var filePath = new FPath(entry["path"]!.GetValue<string>()).ToAbsolute();
            var contentType = entry["contentType"]?.GetValue<string>();
            var metadata = entry["metadata"] is { } m ? ToNodeObject(m, $"the metadata of '{filePath}' in {OutputsManifest}") : null;

            result[filePath] = (contentType, metadata);
        }

        return result;
    }

    private static string ToDiskPath(string folder, FPath path)
    {
        return Path.Combine(folder, path.FullName.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }
}
