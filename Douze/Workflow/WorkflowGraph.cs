using System.Text;
using System.Text.RegularExpressions;
using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// A validated workflow: one step instance per definition, in topological order.
/// </summary>
internal sealed partial class WorkflowGraph
{
    public const string ReservedName = Provenance.SourceStep;

    private readonly Dictionary<string, GraphNode> _byName;
    private readonly Dictionary<string, IReadOnlySet<string>> _ancestors = new(StringComparer.Ordinal);
    private readonly WorkflowDefinition _definition;

    private WorkflowGraph(WorkflowDefinition definition, IReadOnlyList<GraphNode> nodes, IReadOnlyList<string> warnings)
    {
        _definition = definition;
        Nodes = nodes;
        Warnings = warnings;
        _byName = nodes.ToDictionary(x => x.Definition.Name, StringComparer.Ordinal);
        Roots = nodes.Where(x => x.Inputs != null && x.Needs.Count == 0).ToArray();
        Terminals = nodes.Where(x => x.Dependents.Count == 0).ToArray();

        foreach (var node in nodes)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal);

            foreach (var need in node.Needs)
            {
                ancestors.Add(need.Definition.Name);
                ancestors.UnionWith(_ancestors[need.Definition.Name]);
            }

            _ancestors[node.Definition.Name] = ancestors;
        }
    }

    /// <summary>
    /// The nodes in topological order, ties broken by name (ordinal).
    /// </summary>
    public IReadOnlyList<GraphNode> Nodes { get; }

    public GraphNode this[string name] => _byName[name];

    /// <summary>
    /// Nodes with inputs and no needs.
    /// </summary>
    public IReadOnlyList<GraphNode> Roots { get; }

    /// <summary>
    /// Nodes nobody needs.
    /// </summary>
    public IReadOnlyList<GraphNode> Terminals { get; }

    /// <summary>
    /// Validation warnings, such as a workflow without a <c>write</c> step.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlySet<string> Ancestors(string name) => _ancestors[name];

    /// <summary>
    /// Validates <paramref name="definition"/> and instantiates its steps through <paramref name="factory"/>
    /// (<see cref="StepFactory.Create"/> by default).
    /// </summary>
    /// <exception cref="WorkflowValidationException">Every validation error of the definition.</exception>
    public static WorkflowGraph Build(WorkflowDefinition definition, Func<StepDefinition, Step>? factory = null)
    {
        factory ??= StepFactory.Create;

        var errors = new List<WorkflowValidationError>();
        var definitions = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);

        foreach (var step in definition.Steps)
        {
            if (step.Name == ReservedName)
            {
                errors.Add(new($"'{ReservedName}' is a reserved step name", step.Location));
            }
            else if (!definitions.TryAdd(step.Name, step))
            {
                errors.Add(new($"step '{step.Name}' is defined twice", step.Location));
            }
        }

        var names = definitions.Keys.ToArray();

        foreach (var step in definitions.Values)
        {
            foreach (var need in step.Needs)
            {
                if (!definitions.ContainsKey(need))
                {
                    errors.Add(new($"step '{step.Name}' needs unknown step '{need}'{WorkflowValidationException.Suggest(need, names)}", step.Location));
                }
            }

            if (step.Needs.IsEmpty && step.Inputs.IsEmpty)
            {
                errors.Add(new($"step '{step.Name}' has no 'needs' and no 'inputs', so nothing enters it", step.Location));
            }

            if (!step.Outputs.IsEmpty && (step.Run is null || step.Mode != CommandMode.Batch))
            {
                errors.Add(new("'outputs' is only valid with 'mode: batch'", step.Location));
            }
        }

        var order = errors.Count == 0 ? TopologicalOrder(definitions, errors) : [];

        // Steps are instantiated even when the graph is invalid, so every option error is reported at once.
        var steps = new Dictionary<string, Step>(StringComparer.Ordinal);
        var globs = new Dictionary<string, (GlobSet? Inputs, GlobSet? Match)>(StringComparer.Ordinal);

        foreach (var step in definitions.Values)
        {
            try
            {
                var instance = factory(step);

                if (step.GroupBy is not null && !instance.SupportsGrouping)
                {
                    errors.Add(new($"step '{step.Name}' does not support 'group-by'", step.Location));
                }

                steps[step.Name] = instance;
            }
            catch (WorkflowValidationException e)
            {
                errors.AddRange(e.Errors);
            }

            globs[step.Name] = (ParseGlobs(step, step.Inputs, errors), ParseGlobs(step, step.Match, errors));
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        var nodes = new List<GraphNode>(order.Count);
        var nodesByName = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        foreach (var name in order)
        {
            var step = definitions[name];
            var instance = steps[name];
            var (inputs, match) = globs[name];
            var node = new GraphNode(step, instance, nodes.Count, step.Needs.Distinct().Select(x => nodesByName[x]).ToArray(), inputs, match ?? instance.DefaultMatch);

            foreach (var need in node.Needs)
            {
                need.AddDependent(node);
            }

            nodes.Add(node);
            nodesByName[name] = node;
        }

        var warnings = new List<string>();

        if (!definitions.Values.Any(x => x.Uses == "write"))
        {
            warnings.Add("no 'write' step: nothing will be written to the output folder");
        }

        return new WorkflowGraph(definition, nodes, warnings);
    }

    /// <summary>
    /// Kahn's algorithm, always picking the smallest name among ready nodes so that the order does not depend on the
    /// definition order.
    /// </summary>
    private static List<string> TopologicalOrder(Dictionary<string, StepDefinition> definitions, List<WorkflowValidationError> errors)
    {
        var remaining = definitions.Values.ToDictionary(x => x.Name, x => x.Needs.Distinct().Count(), StringComparer.Ordinal);
        var dependents = definitions.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var step in definitions.Values)
        {
            foreach (var need in step.Needs.Distinct())
            {
                dependents[need].Add(step.Name);
            }
        }

        var ready = new SortedSet<string>(remaining.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
        var order = new List<string>(definitions.Count);

        while (ready.Count > 0)
        {
            var name = ready.Min!;
            ready.Remove(name);
            order.Add(name);

            foreach (var dependent in dependents[name])
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (order.Count < definitions.Count)
        {
            var cycle = FindCycle(definitions, remaining.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet(StringComparer.Ordinal));
            errors.Add(new($"steps form a cycle: {string.Join(" -> ", cycle)}", definitions[cycle[0]].Location));
        }

        return order;
    }

    /// <summary>
    /// Finds a cycle among <paramref name="candidates"/>, following data flow (a need, then the step that needs it),
    /// starting from the smallest name.
    /// </summary>
    private static List<string> FindCycle(Dictionary<string, StepDefinition> definitions, HashSet<string> candidates)
    {
        // Walk needs backwards: in a set where every node still has an unresolved need, a walk always closes a cycle.
        var start = candidates.Order(StringComparer.Ordinal).First();
        var path = new List<string>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = start;

        while (!positions.ContainsKey(current))
        {
            positions[current] = path.Count;
            path.Add(current);
            current = definitions[current].Needs.Where(candidates.Contains).Order(StringComparer.Ordinal).First();
        }

        // path holds consumers followed by their needs: reverse it to follow the data flow.
        var cycle = path.Skip(positions[current]).Reverse().ToList();

        // Start from the smallest name so the message is stable.
        var first = cycle.IndexOf(cycle.Order(StringComparer.Ordinal).First());
        cycle = [.. cycle.Skip(first), .. cycle.Take(first)];
        cycle.Add(cycle[0]);
        return cycle;
    }

    private static GlobSet? ParseGlobs(StepDefinition step, IReadOnlyList<string> patterns, List<WorkflowValidationError> errors)
    {
        if (patterns.Count == 0)
        {
            return null;
        }

        var valid = true;

        foreach (var pattern in patterns)
        {
            try
            {
                Glob.Parse(pattern);
            }
            catch (GlobFormatException e)
            {
                errors.Add(new($"invalid pattern '{pattern}': {e.Message}", step.Location));
                valid = false;
            }
        }

        return valid ? GlobSet.Parse(patterns) : null;
    }

    /// <summary>
    /// The effective workflow as YAML, one flow mapping per step, in definition order.
    /// </summary>
    public string ToYaml()
    {
        var builder = new StringBuilder("steps:\n");
        var width = _definition.Steps.Length == 0 ? 0 : _definition.Steps.Max(x => x.Name.Length) + 1;

        foreach (var step in _definition.Steps)
        {
            var fields = new List<string>();

            AddList(fields, "inputs", step.Inputs);
            AddList(fields, "needs", step.Needs);
            AddList(fields, "match", step.Match);

            if (step.Uses != null)
            {
                fields.Add($"uses: {Scalar(step.Uses)}");
            }

            if (step.Run != null)
            {
                fields.Add($"run: {Scalar(step.Run)}");
                fields.Add($"mode: {step.Mode.ToString().ToLowerInvariant()}");
            }

            if (step.GroupBy != null)
            {
                fields.Add($"group-by: {Scalar(step.GroupBy)}");
            }

            if (step.With.Count > 0)
            {
                fields.Add($"with: {FlowNode(step.With)}");
            }

            AddList(fields, "outputs", step.Outputs);
            AddList(fields, "sources", step.Sources);

            if (!step.Incremental)
            {
                fields.Add("incremental: false");
            }

            if (step.Version.Length > 0)
            {
                fields.Add($"version: {Scalar(step.Version)}");
            }

            builder.Append("  ").Append((step.Name + ":").PadRight(width)).Append(" { ").AppendJoin(", ", fields).Append(" }\n");
        }

        return builder.ToString();

        static void AddList(List<string> fields, string name, IReadOnlyList<string> values)
        {
            switch (values.Count)
            {
                case 0:
                    break;
                case 1:
                    fields.Add($"{name}: {Scalar(values[0])}");
                    break;
                default:
                    fields.Add($"{name}: [{string.Join(", ", values.Select(Scalar))}]");
                    break;
            }
        }
    }

    private static string FlowNode(Node? node)
    {
        return node switch
        {
            null => "null",
            NodeObject obj => "{ " + string.Join(", ", obj.Select(x => $"{x.Key}: {FlowNode(x.Value)}")) + " }",
            NodeArray array => "[" + string.Join(", ", array.Select(FlowNode)) + "]",
            NodeValue<string> s => Scalar(s.Value),
            NodeValue<bool> b => b.Value ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => Scalar(node.ToString() ?? ""),
        };
    }

    private static string Scalar(string value)
    {
        return PlainScalar().IsMatch(value) ? value : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    [GeneratedRegex(@"^[A-Za-z0-9_./-][A-Za-z0-9_./ -]*$")]
    private static partial Regex PlainScalar();
}

internal sealed class GraphNode
{
    private readonly List<GraphNode> _dependents = [];

    internal GraphNode(StepDefinition definition, Step step, int index, IReadOnlyList<GraphNode> needs, GlobSet? inputs, GlobSet match)
    {
        Definition = definition;
        Step = step;
        Index = index;
        Needs = needs;
        Inputs = inputs;
        Match = match;
    }

    public StepDefinition Definition { get; }

    public Step Step { get; }

    /// <summary>
    /// The topological index.
    /// </summary>
    public int Index { get; }

    public IReadOnlyList<GraphNode> Needs { get; }

    public IReadOnlyList<GraphNode> Dependents => _dependents;

    /// <summary>
    /// The source files that enter the node, or <c>null</c> when it has no <c>inputs</c>.
    /// </summary>
    public GlobSet? Inputs { get; }

    /// <summary>
    /// The entering files the step consumes: the definition's <c>match</c> or the step's default.
    /// </summary>
    public GlobSet Match { get; }

    internal void AddDependent(GraphNode node) => _dependents.Add(node);

    public override string ToString() => Definition.Name;
}
