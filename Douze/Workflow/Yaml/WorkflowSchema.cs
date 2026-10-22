using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Douze.Nodes;
using SharpYaml.Model;

namespace Douze.Workflow.Yaml;

/// <summary>
/// The fields of a workflow file (section 4 of the workflow engine plan): how each one is read from YAML into a
/// <see cref="StepDefinition"/> and written back by <see cref="ToYaml"/>.
/// </summary>
internal static partial class WorkflowSchema
{
    public const int MaxStepNameLength = 64;

    public static readonly ImmutableArray<string> TopLevelKeys = ["site", "markdown", "steps"];

    public static readonly ImmutableArray<string> SiteKeys = ["content", "data", "includes", "shortcodes", "output", "url", "parallelism"];

    public static readonly ImmutableArray<string> MarkdownKeys = ["extensions", "aside", "links", "code", "images"];

    public static readonly ImmutableArray<string> AsideKeys = ["tag", "class", "kinds", "titles", "icons", "titles-from"];

    public static readonly ImmutableArray<string> CodeKeys = ["class-prefix", "passthrough"];

    public static readonly ImmutableArray<string> ImageKeys = ["widths", "formats", "quality", "output", "loading"];

    /// <summary>
    /// The keys as a message lists them: <c>'a', 'b' or 'c'</c>.
    /// </summary>
    public static string Expected(ImmutableArray<string> keys)
    {
        return keys.Length == 1 ? $"'{keys[0]}'" : string.Join(", ", keys[..^1].Select(x => $"'{x}'")) + $" or '{keys[^1]}'";
    }

    /// <summary>
    /// The step fields, in the order <see cref="ToYaml"/> writes them.
    /// </summary>
    public static readonly ImmutableArray<WorkflowField> StepFields =
    [
        Globs("inputs", x => x.Inputs, (x, v) => x with { Inputs = v }),
        Names("needs", x => x.Needs, (x, v) => x with { Needs = v }),
        Globs("match", x => x.Match, (x, v) => x with { Match = v }),
        String("uses", x => x.Uses, (x, v) => x with { Uses = v }),
        String("run", x => x.Run, (x, v) => x with { Run = v }),
        Enum("mode", CommandMode.Batch, x => x.Mode, (x, v) => x with { Mode = v }, commandOnly: true),
        String("group-by", x => x.GroupBy, (x, v) => x with { GroupBy = v }),
        Mapping("with", x => x.With, (x, v) => x with { With = v }),
        Globs("outputs", x => x.Outputs, (x, v) => x with { Outputs = v }, commandOnly: true),
        Globs("sources", x => x.Sources, (x, v) => x with { Sources = v }, commandOnly: true),
        Bool("incremental", true, x => x.Incremental, (x, v) => x with { Incremental = v }),
        String("version", x => x.Version, (x, v) => x with { Version = v }, commandOnly: true),
        StringMap("env", x => x.Env, (x, v) => x with { Env = v }, commandOnly: true),
        String("cwd", x => x.WorkingDirectory, (x, v) => x with { WorkingDirectory = v }, commandOnly: true),
        Duration("timeout", x => x.Timeout, (x, v) => x with { Timeout = v }, commandOnly: true),
        Enum("stderr", StderrPolicy.Fail, x => x.Stderr, (x, v) => x with { Stderr = v }, commandOnly: true),
        Bool("shell", true, x => x.Shell, (x, v) => x with { Shell = v }, commandOnly: true),
    ];

    public static readonly FrozenDictionary<string, WorkflowField> StepFieldsByName = StepFields.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);

    public static bool IsValidStepName(string name) => StepName().IsMatch(name);

    /// <summary>
    /// The workflow as YAML, one flow mapping per step, in definition order. Fields with their default value are left
    /// out. Loading the result gives the same definitions.
    /// </summary>
    public static string ToYaml(WorkflowDefinition workflow)
    {
        if (workflow.Steps.IsEmpty)
        {
            return "steps: {}\n";
        }

        var builder = new StringBuilder("steps:\n");
        var width = workflow.Steps.Max(x => Scalar(x.Name).Length) + 1;

        foreach (var step in workflow.Steps)
        {
            var fields = StepFields.Select(x => (x.Name, Value: x.Write(step))).Where(x => x.Value != null).Select(x => $"{x.Name}: {x.Value}");

            builder.Append("  ").Append((Scalar(step.Name) + ":").PadRight(width)).Append(" { ").AppendJoin(", ", fields).Append(" }\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A scalar as YAML: plain when it reads back as the same string, double quoted otherwise.
    /// </summary>
    public static string Scalar(string value)
    {
        if (PlainScalar().IsMatch(value) && !YamlSource.IsTyped(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ' or '\u007f': builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: builder.Append(c); break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// A node as flow YAML that <see cref="YamlSource.ToNode"/> reads back as an equal node.
    /// </summary>
    public static string FlowNode(Node? node)
    {
        return node switch
        {
            null => "null",
            NodeObject obj => obj.Count == 0 ? "{}" : "{ " + string.Join(", ", obj.Select(x => $"{Scalar(x.Key)}: {FlowNode(x.Value)}")) + " }",
            NodeArray array => "[" + string.Join(", ", array.Select(FlowNode)) + "]",
            NodeValue<string> s => Scalar(s.Value),
            NodeValue<bool> b => b.Value ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Scalar(node.ToString() ?? ""),
        };
    }

    /// <summary>
    /// Parses <c>30s</c>, <c>5m</c>, <c>1h</c> or <c>500ms</c>.
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        var match = DurationPattern().Match(text);

        if (!match.Success || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount == 0 || amount > int.MaxValue)
        {
            duration = default;
            return false;
        }

        duration = match.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromHours(amount),
        };

        return true;
    }

    /// <summary>
    /// A duration in the largest unit that divides it: <c>90s</c>, <c>5m</c>, <c>1h</c>.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var ms = (long)duration.TotalMilliseconds;

        return ms % 3_600_000 == 0 ? $"{ms / 3_600_000}h"
            : ms % 60_000 == 0 ? $"{ms / 60_000}m"
            : ms % 1_000 == 0 ? $"{ms / 1_000}s"
            : $"{ms}ms";
    }

    private static string? List(ImmutableArray<string> values)
    {
        return values.Length switch
        {
            0 => null,
            1 => Scalar(values[0]),
            _ => "[" + string.Join(", ", values.Select(Scalar)) + "]",
        };
    }

    private static WorkflowField Globs(string name, Func<StepDefinition, ImmutableArray<string>> get, Func<StepDefinition, ImmutableArray<string>, StepDefinition> set, bool commandOnly = false)
    {
        return new WorkflowField(name, commandOnly, "a glob or a list of globs", (yaml, node, step, errors) =>
        {
            if (!TryReadStrings(yaml, name, "a glob or a list of globs", node, errors, out var values, out var offending))
            {
                return step;
            }

            var valid = true;

            for (var i = 0; i < values.Length; i++)
            {
                try
                {
                    Glob.Parse(values[i]);
                }
                catch (GlobFormatException e)
                {
                    errors.Add(new($"invalid pattern '{values[i]}': {e.Message}", yaml.Location(offending[i])));
                    valid = false;
                }
            }

            return valid ? set(step, values) : step;
        }, step => List(get(step)));
    }

    private static WorkflowField Names(string name, Func<StepDefinition, ImmutableArray<string>> get, Func<StepDefinition, ImmutableArray<string>, StepDefinition> set)
    {
        return new WorkflowField(name, false, "a string or a list of strings",
            (yaml, node, step, errors) => TryReadStrings(yaml, name, "a string or a list of strings", node, errors, out var values, out _) ? set(step, values) : step,
            step => List(get(step)));
    }

    private static WorkflowField String(string name, Func<StepDefinition, string?> get, Func<StepDefinition, string, StepDefinition> set, bool commandOnly = false)
    {
        return new WorkflowField(name, commandOnly, "a string",
            (_, node, step, _) => YamlSource.Scalar(node) is { } value ? set(step, value) : null,
            step => get(step) is { Length: > 0 } value ? Scalar(value) : null);
    }

    private static WorkflowField Bool(string name, bool defaultValue, Func<StepDefinition, bool> get, Func<StepDefinition, bool, StepDefinition> set, bool commandOnly = false)
    {
        return new WorkflowField(name, commandOnly, "true or false",
            (_, node, step, _) => YamlSource.Scalar(node) is { } text && bool.TryParse(text, out var value) ? set(step, value) : null,
            step => get(step) == defaultValue ? null : get(step) ? "true" : "false");
    }

    private static WorkflowField Enum<T>(string name, T defaultValue, Func<StepDefinition, T> get, Func<StepDefinition, T, StepDefinition> set, bool commandOnly = false)
        where T : struct, Enum
    {
        var values = System.Enum.GetValues<T>();
        var names = values.Select(x => x.ToString().ToLowerInvariant()).ToArray();
        var expected = string.Join(", ", names[..^1].Select(x => $"'{x}'")) + $" or '{names[^1]}'";

        return new WorkflowField(name, commandOnly, expected, (_, node, step, _) =>
        {
            var index = Array.IndexOf(names, YamlSource.Scalar(node));
            return index < 0 ? null : set(step, values[index]);
        }, step => EqualityComparer<T>.Default.Equals(get(step), defaultValue) ? null : names[Array.IndexOf(values, get(step))]);
    }

    private static WorkflowField Mapping(string name, Func<StepDefinition, NodeObject> get, Func<StepDefinition, NodeObject, StepDefinition> set)
    {
        return new WorkflowField(name, false, "a mapping",
            (yaml, node, step, errors) => node is YamlMapping ? set(step, (NodeObject)yaml.ToNode(node, errors)!) : null,
            step => get(step).Count == 0 ? null : FlowNode(get(step)));
    }

    private static WorkflowField StringMap(string name, Func<StepDefinition, ImmutableDictionary<string, string>> get, Func<StepDefinition, ImmutableDictionary<string, string>, StepDefinition> set, bool commandOnly = false)
    {
        return new WorkflowField(name, commandOnly, "a mapping of names to strings", (yaml, node, step, errors) =>
        {
            if (node is not YamlMapping mapping)
            {
                return null;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

            foreach (var (key, _, value) in yaml.Entries(mapping, errors))
            {
                if (value is null || YamlSource.Scalar(value) is not { } text)
                {
                    errors.Add(new($"'{name}' must be a mapping of names to strings", yaml.Location(value ?? node)));
                    return step;
                }

                builder[key] = text;
            }

            return set(step, builder.ToImmutable());
        }, step => get(step).IsEmpty ? null : "{ " + string.Join(", ", get(step).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{Scalar(x.Key)}: {Scalar(x.Value)}")) + " }");
    }

    private static WorkflowField Duration(string name, Func<StepDefinition, TimeSpan?> get, Func<StepDefinition, TimeSpan, StepDefinition> set, bool commandOnly = false)
    {
        return new WorkflowField(name, commandOnly, "a duration", (yaml, node, step, errors) =>
        {
            if (YamlSource.Scalar(node) is not { } text || !TryParseDuration(text, out var duration))
            {
                errors.Add(new($"invalid {name} '{YamlSource.Scalar(node) ?? "..."}'; use 30s, 5m or 1h", yaml.Location(node)));
                return step;
            }

            return set(step, duration);
        }, step => get(step) is { } value ? FormatDuration(value) : null);
    }

    /// <summary>
    /// Reads a scalar or a sequence of scalars, or reports the offending node.
    /// </summary>
    private static bool TryReadStrings(YamlSource yaml, string name, string expected, YamlElement node, ICollection<WorkflowValidationError> errors, out ImmutableArray<string> values, out ImmutableArray<YamlElement> nodes)
    {
        if (YamlSource.Scalar(node) is { } single)
        {
            values = [single];
            nodes = [node];
            return true;
        }

        values = default;
        nodes = default;

        if (node is not YamlSequence sequence)
        {
            errors.Add(new($"'{name}' must be {expected}", yaml.Location(node)));
            return false;
        }

        var valueBuilder = ImmutableArray.CreateBuilder<string>(sequence.Count);
        var nodeBuilder = ImmutableArray.CreateBuilder<YamlElement>(sequence.Count);

        foreach (var item in sequence)
        {
            if (item is null || YamlSource.Scalar(item) is not { } value)
            {
                errors.Add(new($"'{name}' must be {expected}", yaml.Location(item ?? node)));
                return false;
            }

            valueBuilder.Add(value);
            nodeBuilder.Add(item);
        }

        values = valueBuilder.MoveToImmutable();
        nodes = nodeBuilder.MoveToImmutable();
        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex StepName();

    // Text that a plain flow scalar reads back as is: no indicators, no leading or trailing space.
    [GeneratedRegex(@"^[A-Za-z0-9_./][A-Za-z0-9_./ -]*(?<! )$")]
    private static partial Regex PlainScalar();

    [GeneratedRegex("^([0-9]+)(ms|s|m|h)$")]
    private static partial Regex DurationPattern();
}

/// <summary>
/// One field of a step in a workflow file.
/// </summary>
internal sealed class WorkflowField
{
    /// <summary>
    /// Returns the step with the field set, the step unchanged after adding specific errors, or <c>null</c> when the
    /// node has the wrong type.
    /// </summary>
    public delegate StepDefinition? Reader(YamlSource yaml, YamlElement node, StepDefinition step, ICollection<WorkflowValidationError> errors);

    private readonly Reader _read;
    private readonly Func<StepDefinition, string?> _write;

    public WorkflowField(string name, bool commandOnly, string expected, Reader read, Func<StepDefinition, string?> write)
    {
        Name = name;
        CommandOnly = commandOnly;
        Expected = expected;
        _read = read;
        _write = write;
    }

    public string Name { get; }

    /// <summary>
    /// Whether the field is only valid with <c>run</c>.
    /// </summary>
    public bool CommandOnly { get; }

    /// <summary>
    /// What the value must be, as in <c>'needs' must be a string or a list of strings</c>.
    /// </summary>
    public string Expected { get; }

    public StepDefinition Read(YamlSource yaml, YamlElement node, StepDefinition step, ICollection<WorkflowValidationError> errors)
    {
        if (_read(yaml, node, step, errors) is { } result)
        {
            return result;
        }

        errors.Add(new($"'{Name}' must be {Expected}", yaml.Location(node)));
        return step;
    }

    /// <summary>
    /// The value as flow YAML, or <c>null</c> when it is the default.
    /// </summary>
    public string? Write(StepDefinition step) => _write(step);
}
