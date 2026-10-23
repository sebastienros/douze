using System.Buffers;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// The configuration of one step, from a workflow file or from the default workflow.
/// </summary>
internal sealed record StepDefinition
{
    private static readonly ConditionalWeakTable<StepDefinition, object> ConfigHashes = new();

    public required string Name { get; init; }
    public ImmutableArray<string> Needs { get; init; } = [];
    public ImmutableArray<string> Inputs { get; init; } = [];     // globs over the source tree
    public ImmutableArray<string> Match { get; init; } = [];      // globs over entering files; empty = the step's default
    public string? Uses { get; init; }                             // built-in id
    public NodeObject With { get; init; } = new();                 // options for `uses`
    public string? Run { get; init; }                              // command line
    public string? Tool { get; init; }                             // a tool Douze downloads for the command (NpmTool.Known), `name` or `name@version`
    public CommandMode Mode { get; init; } = CommandMode.Batch;    // filter | batch | worker
    public string? GroupBy { get; init; }                          // "file" | "dir" | Liquid template
    public ImmutableArray<string> Outputs { get; init; } = [];    // globs read back after a batch command
    public bool Incremental { get; init; } = true;
    public string Version { get; init; } = "";
    public ImmutableArray<string> Sources { get; init; } = [];    // globs of files that participate in the command's cache key
    public ImmutableDictionary<string, string> Env { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    public StderrPolicy Stderr { get; init; } = StderrPolicy.Fail;
    public bool Shell { get; init; } = true;
    public bool Enabled { get; init; } = true;                     // the 'enabled' expression; false: the step is left out, its consumers need its needs
    public SourceLocation? Location { get; init; }                 // YAML file, line, column; null for the default workflow

    public bool IsCommand => Run is not null;

    /// <summary>
    /// The hash of every field except <see cref="Name"/>, <see cref="Needs"/> and <see cref="Location"/>, plus Douze's version.
    /// Computed once per instance (a 'with' expression creates a new instance, so it never sees a stale value).
    /// </summary>
    public Hash128 ConfigHash => (Hash128)ConfigHashes.GetValue(this, static d => d.ComputeConfigHash());

    private object ComputeConfigHash()
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteArray(writer, "inputs", Inputs);
            WriteArray(writer, "match", Match);
            writer.WriteString("uses", Uses);
            writer.WritePropertyName("with");
            CanonicalJson.Write(writer, With);
            writer.WriteString("run", Run);
            writer.WriteString("tool", Tool);
            writer.WriteString("mode", Mode.ToString());
            writer.WriteString("group-by", GroupBy);
            WriteArray(writer, "outputs", Outputs);
            writer.WriteBoolean("incremental", Incremental);
            writer.WriteString("version", Version);
            WriteArray(writer, "sources", Sources);
            writer.WriteStartObject("env");

            foreach (var entry in Env.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                writer.WriteString(entry.Key, entry.Value);
            }

            writer.WriteEndObject();
            writer.WriteString("cwd", WorkingDirectory);
            writer.WriteString("timeout", Timeout?.ToString("c"));
            writer.WriteString("stderr", Stderr.ToString());
            writer.WriteBoolean("shell", Shell);
            writer.WriteString("douze", DouzeVersion);
            writer.WriteEndObject();
        }

        return Hash128.Of(buffer.WrittenSpan);

        static void WriteArray(Utf8JsonWriter writer, string name, ImmutableArray<string> values)
        {
            writer.WriteStartArray(name);

            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Douze's informational version, part of every configuration hash so that a new version invalidates cached results.
    /// </summary>
    public static readonly string DouzeVersion =
        typeof(StepDefinition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
}

internal enum CommandMode { Filter, Batch, Worker }

internal enum StderrPolicy { Fail, Warn, Ignore }

internal readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}({Line},{Column})";
}
