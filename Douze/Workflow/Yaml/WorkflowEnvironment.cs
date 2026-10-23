using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using Douze.Liquid;
using Douze.Nodes;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Yaml;

/// <summary>
/// What the <c>enabled</c> expression of a step sees: <c>os</c> (<c>windows</c>, <c>linux</c>, <c>macos</c> or
/// <c>other</c>), <c>arch</c> (<c>x64</c>, <c>arm64</c>, ...), <c>env</c> (the environment variables), <c>watch</c>
/// (true for <c>douze watch</c> and <c>douze serve</c>) and <c>vars</c> (<see cref="ResolveVars"/>). The values are the
/// same for every step of a run.
/// </summary>
internal sealed class WorkflowEnvironment
{
    /// <summary>
    /// The prefix of the environment variables that set variables: <c>DOUZE_VARS__og</c> sets <c>og</c>.
    /// </summary>
    public const string VarsPrefix = "DOUZE_VARS__";

    private static readonly FluidParser Parser = LiquidOptions.CreateParser();
    private static readonly TemplateOptions Options = new TemplateOptionsBuilder().Build();

    private readonly Dictionary<string, string> _env;
    private readonly ImmutableArray<VarOverride> _overrides;
    private readonly Dictionary<string, FluidValue> _values;

    // overrides: the --var and --env options, in order.
    public WorkflowEnvironment(string os, string arch, IReadOnlyDictionary<string, string> env, bool watch, IEnumerable<VarOverride>? overrides = null)
    {
        // Environment variable names are case insensitive on Windows.
        _env = new Dictionary<string, string>(env, os == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _overrides = [.. overrides ?? []];

        var variables = new Dictionary<string, FluidValue>(_env.Comparer);

        foreach (var (name, value) in _env)
        {
            variables[name] = StringValue.Create(value);
        }

        _values = new(StringComparer.Ordinal)
        {
            ["os"] = StringValue.Create(os),
            ["arch"] = StringValue.Create(arch),
            ["env"] = DictionaryValue.Create(new Variables(variables)),
            ["watch"] = BooleanValue.Create(watch),
            ["vars"] = NodeValues.ToFluidValue(new NodeObject()),
        };
    }

    private WorkflowEnvironment(WorkflowEnvironment source, NodeObject vars)
    {
        _env = source._env;
        _overrides = source._overrides;
        _values = new(source._values, StringComparer.Ordinal) { ["vars"] = NodeValues.ToFluidValue(vars) };
    }

    /// <summary>
    /// The environment of this process.
    /// </summary>
    public static WorkflowEnvironment Current(bool watch, IEnumerable<VarOverride>? overrides = null)
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "other";

        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = (string?)entry.Value ?? "";
        }

        return new WorkflowEnvironment(os, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), env, watch, overrides);
    }

    /// <summary>
    /// The same environment, whose expressions see <paramref name="vars"/> as <c>vars</c>.
    /// </summary>
    public WorkflowEnvironment WithVars(NodeObject vars) => new(this, vars);

    /// <summary>
    /// The variables of the <c>vars</c> section, set by the <c>DOUZE_VARS__&lt;name&gt;</c> environment variables (in
    /// ordinal order), then by the <c>--var</c> and <c>--env</c> options (in order), each over the previous one. Only
    /// declared variables can be set, matching their names case insensitively, and the value takes the type of the
    /// declared one: <c>--var og=false</c> is the boolean <c>false</c> when <c>og</c> is a boolean.
    /// </summary>
    /// <param name="declared">The <c>vars</c> section.</param>
    /// <param name="errors">Receives the unknown variables and the values of the wrong type.</param>
    public NodeObject ResolveVars(NodeObject declared, ICollection<WorkflowValidationError> errors)
    {
        var result = (NodeObject)declared.Clone();

        foreach (var (name, value) in _env.Where(x => x.Key.StartsWith(VarsPrefix, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            Set(name[VarsPrefix.Length..], value, $"environment variable '{name}'", secret: false);
        }

        foreach (var item in _overrides)
        {
            if (item.Variable == null)
            {
                Set(item.Name, item.Value!, $"'--var {item.Name}'", secret: false);
            }
            else if (_env.TryGetValue(item.Variable, out var value))
            {
                // The value comes from the environment to stay out of the command line: errors do not show it.
                Set(item.Name, value, $"'--env {item.Name}={item.Variable}'", secret: true);
            }
        }

        return result;

        void Set(string name, string text, string origin, bool secret)
        {
            var key = declared.Select(x => x.Key).FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

            if (key == null)
            {
                errors.Add(new($"unknown variable '{name}' in {origin}; declare it in 'vars'{WorkflowValidationException.Suggest(name, declared.Select(x => x.Key))}", null));
                return;
            }

            Node? value = declared[key] switch
            {
                NodeObject or NodeArray => null,
                NodeValue<bool> => bool.TryParse(text, out var b) ? NodeValue.Create(b) : null,
                NodeValue<int> => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? NodeValue.Create(i) : null,
                NodeValue<decimal> => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? NodeValue.Create(d) : null,
                _ => NodeValue.Create(text),
            };

            if (value == null)
            {
                errors.Add(new(declared[key] switch
                {
                    NodeObject => $"variable '{key}' is an object and cannot be set by {origin}",
                    NodeArray => $"variable '{key}' is a list and cannot be set by {origin}",
                    var other => $"variable '{key}' must be {(other is NodeValue<bool> ? "true or false" : other is NodeValue<int> ? "an integer" : "a number")}"
                        + (secret ? $"; the value of {origin} is not" : $", not '{text}' ({origin})"),
                }, null));
                return;
            }

            result[key] = value;
        }
    }

    /// <summary>
    /// Evaluates a Liquid expression, as in <c>{% if expression %}</c>.
    /// </summary>
    /// <exception cref="DouzeException">The expression is invalid, or fails.</exception>
    public bool Evaluate(string expression)
    {
        if (!Parser.TryParse($"{{% if {expression} %}}true{{% endif %}}", out var template, out var error))
        {
            var end = error.IndexOf('\n');
            throw new DouzeException($"invalid expression '{expression}': {(end >= 0 ? error[..end] : error).TrimEnd()}");
        }

        var context = new TemplateContext(Options);

        foreach (var (name, value) in _values)
        {
            context.SetValue(name, value);
        }

        try
        {
            // Nothing in the expression waits: the task is complete.
            return template.RenderAsync(context, NullEncoder.Default).AsTask().GetAwaiter().GetResult() == "true";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new DouzeException($"expression '{expression}' failed: {e.Message}");
        }
    }

    // Not FluidValueDictionaryFluidIndexable, which copies the dictionary without its comparer.
    private sealed class Variables(Dictionary<string, FluidValue> values) : IFluidIndexable
    {
        public int Count => values.Count;

        public IEnumerable<string> Keys => values.Keys;

        public bool TryGetValue(string name, out FluidValue value) => values.TryGetValue(name, out value!);
    }
}

/// <summary>
/// A <c>--var name=value</c> option (<see cref="Value"/>), or a <c>--env name=VARIABLE</c> option
/// (<see cref="Variable"/>), which sets the variable to the value of an environment variable when it is set.
/// </summary>
internal sealed record VarOverride(string Name, string? Value, string? Variable)
{
    public static VarOverride FromValue(string name, string value) => new(name, value, null);

    public static VarOverride FromVariable(string name, string variable) => new(name, null, variable);
}
