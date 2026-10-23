using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using Douze.Liquid;
using Fluid;
using Fluid.Values;

namespace Douze.Workflow.Yaml;

/// <summary>
/// What the <c>enabled</c> expression of a step sees: <c>os</c> (<c>windows</c>, <c>linux</c>, <c>macos</c> or
/// <c>other</c>), <c>arch</c> (<c>x64</c>, <c>arm64</c>, ...), <c>env</c> (the environment variables) and <c>watch</c>
/// (true for <c>douze watch</c> and <c>douze serve</c>). The values are the same for every step of a run.
/// </summary>
internal sealed class WorkflowEnvironment
{
    private static readonly FluidParser Parser = LiquidOptions.CreateParser();
    private static readonly TemplateOptions Options = new TemplateOptionsBuilder().Build();

    private readonly Dictionary<string, FluidValue> _values;

    public WorkflowEnvironment(string os, string arch, IReadOnlyDictionary<string, string> env, bool watch)
    {
        // Environment variable names are case insensitive on Windows.
        var comparer = os == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var variables = new Dictionary<string, FluidValue>(comparer);

        foreach (var (name, value) in env)
        {
            variables[name] = StringValue.Create(value);
        }

        _values = new(StringComparer.Ordinal)
        {
            ["os"] = StringValue.Create(os),
            ["arch"] = StringValue.Create(arch),
            ["env"] = DictionaryValue.Create(new Variables(variables)),
            ["watch"] = BooleanValue.Create(watch),
        };
    }

    /// <summary>
    /// The environment of this process.
    /// </summary>
    public static WorkflowEnvironment Current(bool watch)
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

        return new WorkflowEnvironment(os, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), env, watch);
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
