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
    /// <c>&lt;temp root&gt;/&lt;step&gt;/</c>, the step's temporary folder, created on demand.
    /// </summary>
    protected string TempFolder => Path.Combine(Build.TempRoot, Name);

    public override ValueTask InitializeAsync(BuildContext context, CancellationToken ct)
    {
        _build = context;

        // A site without a source folder on disk (tests, memory file systems) runs its commands in the current folder.
        var root = context.Site.SourceDirectory ?? Environment.CurrentDirectory;
        var directory = Path.GetFullPath(Path.Combine(root, Definition.WorkingDirectory ?? "."));

        if (!Directory.Exists(directory))
        {
            throw new DouzeException($"the working directory '{directory}' does not exist");
        }

        _workingDirectory = directory;
        return default;
    }

    public override ValueTask DisposeAsync()
    {
        if (_build != null)
        {
            // Only empty folders: units delete their own (they are kept with --verbose).
            DeleteIfEmpty(TempFolder);
            DeleteIfEmpty(_build.TempRoot);
        }

        return default;
    }

    protected CommandModel CreateModel(string? inputs = null, string? outputs = null, FPath? file = null)
    {
        return new CommandModel(Name, WorkingDirectory, Build.Site.OutputDirectory, inputs, outputs, file);
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

    private static void DeleteIfEmpty(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Missing, or not empty: another step's folder, or a unit kept with --verbose.
        }
    }
}
