using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Progress;

namespace Douze.Workflow.Commands;

/// <summary>
/// What the command steps share: the parsed <c>run</c> and <c>env</c> templates, the working directory, the environment,
/// and the failure rules (exit code and <c>stderr</c> policy).
/// </summary>
internal abstract class CommandStep : Step
{
    private BuildContext? _build;
    private string? _workingDirectory;
    private Hash128 _cacheVersion;

    protected CommandStep(StepDefinition definition) : base(definition)
    {
        if (definition.Mode != CommandMode.Worker && definition.With.Count > 0)
        {
            throw new WorkflowValidationException($"'with' is only valid with 'uses' or with 'mode: worker' (step '{definition.Name}')", definition.Location);
        }

        Template = CommandTemplate.Parse(definition);
    }

    protected CommandTemplate Template { get; }

    protected BuildContext Build => _build ?? throw new InvalidOperationException($"step '{Name}' is not initialized");

    /// <summary>
    /// The absolute working directory of the command: <c>cwd</c> relative to the source folder.
    /// </summary>
    protected string WorkingDirectory => _workingDirectory ?? throw new InvalidOperationException($"step '{Name}' is not initialized");

    /// <summary>
    /// <c>&lt;temp root&gt;/&lt;step&gt;/</c>, the step's temporary folder, created on demand and deleted with the temp
    /// root at the end of the build.
    /// </summary>
    protected string TempFolder => Path.Combine(Build.TempRoot, Name);

    /// <summary>
    /// What the command's results depend on besides its definition and inputs: its <c>version</c>, and the files of the
    /// source folder that <c>sources</c> matches (the scripts it runs, the data it reads from disk), by path and content.
    /// A command does not see the scopes: its results do not depend on them.
    /// </summary>
    public override Hash128 CacheVersion => _cacheVersion;

    public override IEnumerable<Requirement> Requirements(BuildContext context) => CommandRequirements.Create(Definition, context);

    public override async ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;
        _cacheVersion = await ComputeCacheVersionAsync(context, ct);

        // A site without a source folder on disk (tests, memory file systems) runs its commands in the current folder.
        var root = context.Site.SourceDirectory ?? Environment.CurrentDirectory;
        var directory = Path.GetFullPath(Path.Combine(root, Definition.WorkingDirectory ?? "."));

        if (!Directory.Exists(directory))
        {
            throw new DouzeException($"the working directory '{directory}' does not exist");
        }

        _workingDirectory = directory;

        // The binary of `tool`, downloaded on first use, for `{{ tool }}` in the run line.
        if (CommandRequirements.ToolRequirement(Definition, context) is { } tool)
        {
            _toolPath = await tool.InstallAsync(ct);
        }
    }

    private string? _toolPath;

    private async ValueTask<Hash128> ComputeCacheVersionAsync(BuildContext context, CancellationToken ct)
    {
        var hashes = new List<Hash128> { Hash128.OfText(Definition.Version) };

        if (Definition.Sources.IsEmpty)
        {
            return hashes[0];
        }

        var sources = GlobSet.Parse(Definition.Sources);
        var files = await SourceSnapshot.ListAsync(context.Source, SourceScope.Of([sources]), context.ExcludedSourceFolders);

        foreach (var path in files.Where(sources.IsMatch).Order())
        {
            ct.ThrowIfCancellationRequested();

            if (!(await context.Source.GetFileEntryAsync(path)).Exists)
            {
                // A file a pattern names, which is not there.
                continue;
            }

            await using var stream = await context.Source.OpenFileAsync(path);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);

            hashes.Add(Hash128.OfText(path.FullName));
            hashes.Add(Hash128.Of(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
        }

        return Hash128.Combine([.. hashes]);
    }

    protected CommandModel CreateModel(string? inputs = null, string? outputs = null, FPath? file = null)
    {
        return new CommandModel(Name, WorkingDirectory, Build.Site.OutputDirectory, inputs, outputs, file, _toolPath);
    }

    /// <summary>
    /// Renders the command line and the environment: the step's <c>env</c>, then <paramref name="environment"/>, then
    /// <c>DOUZE_STEP</c>, <c>DOUZE_SITE</c> and <c>DOUZE_SOURCE</c>, which are always set.
    /// </summary>
    protected async ValueTask<ProcessStartInfo> CreateStartInfoAsync(CommandModel model, IEnumerable<KeyValuePair<string, string>> environment, CancellationToken ct)
    {
        var line = await Template.RenderRunAsync(model, Build.LiquidOptions, ct);
        var env = new List<KeyValuePair<string, string>>(await Template.RenderEnvAsync(model, Build.LiquidOptions, ct));

        env.AddRange(environment);
        env.Add(new("DOUZE_STEP", Name));
        env.Add(new("DOUZE_SITE", Build.Site.OutputDirectory ?? ""));
        env.Add(new("DOUZE_SOURCE", Build.Site.SourceDirectory ?? ""));

        return ProcessRunner.CreateStartInfo(line, Definition.Shell, model.WorkingDirectory, env);
    }

    /// <summary>
    /// Applies the failure rules to a command that ran: a non-zero exit code fails the unit, and so does stderr output
    /// with <c>stderr: fail</c>; <c>stderr: warn</c> forwards it as a warning. <paramref name="what"/> names the unit in
    /// messages: <c>" for '/a.md'"</c>, or an empty string.
    /// </summary>
    /// <exception cref="DouzeException">The unit fails.</exception>
    protected void Check(CommandResult result, string what, StepContext context)
    {
        var stderr = ProcessRunner.Tail(result.Stderr);

        if (result.ExitCode != 0)
        {
            throw new DouzeException($"command exited with code {result.ExitCode}{what}{(stderr.Length > 0 ? ": " + stderr : "")}");
        }

        if (stderr.Length == 0)
        {
            return;
        }

        switch (Definition.Stderr)
        {
            case StderrPolicy.Fail:
                throw new DouzeException($"command wrote to stderr{what}: {stderr}");
            case StderrPolicy.Warn:
                context.Log(LogLevel.Warning, $"command wrote to stderr{what}: {stderr}");
                break;
        }
    }

    /// <summary>
    /// The own metadata of a file as JSON (keys sorted).
    /// </summary>
    protected static string MetadataJson(Metadata metadata)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            CanonicalJson.Write(writer, metadata.Own);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Converts a JSON object to the metadata of a file.
    /// </summary>
    /// <exception cref="DouzeException">The value is not an object.</exception>
    protected static NodeObject ToNodeObject(System.Text.Json.Nodes.JsonNode? json, string what)
    {
        return JsonHelper.Convert(json) as NodeObject ?? throw new DouzeException($"{what} must be a JSON object");
    }

    protected static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // A file still open by a process that outlived its command; the folder stays under the temp root.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
