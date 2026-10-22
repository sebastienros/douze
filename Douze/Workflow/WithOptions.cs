using Douze.Nodes;

namespace Douze.Workflow;

/// <summary>
/// Reads the <c>with</c> options of a built-in step. Creating it validates that every key is declared by the step.
/// </summary>
internal sealed class WithOptions
{
    private readonly StepDefinition _definition;

    /// <exception cref="WorkflowValidationException">An option is not in <paramref name="declared"/>.</exception>
    public WithOptions(StepDefinition definition, params string[] declared)
    {
        _definition = definition;

        var errors = new List<WorkflowValidationError>();

        foreach (var property in definition.With.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!declared.Contains(property.Key, StringComparer.Ordinal))
            {
                errors.Add(new WorkflowValidationError(
                    $"unknown option '{property.Key}' for step '{definition.Name}'{WorkflowValidationException.Suggest(property.Key, declared)}",
                    definition.Location));
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }
    }

    public bool Has(string key) => _definition.With.TryGetItem(key, out var value) && value is not null;

    public string? GetString(string key, string? defaultValue = null)
    {
        if (!_definition.With.TryGetItem(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is NodeValue<string> s)
        {
            return s.Value;
        }

        if (value is NodeValue and IFormattable formattable)
        {
            return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        }

        throw TypeError(key, "a string");
    }

    public string GetRequiredString(string key)
    {
        return GetString(key) ?? throw new WorkflowValidationException($"step '{_definition.Name}' needs the option '{key}'", _definition.Location);
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (!_definition.With.TryGetItem(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            NodeValue<bool> b => b.Value,
            NodeValue<string> { Value: "true" } => true,
            NodeValue<string> { Value: "false" } => false,
            _ => throw TypeError(key, "true or false"),
        };
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (!_definition.With.TryGetItem(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            NodeValue<int> i => i.Value,
            NodeValue<long> l when l.Value is >= int.MinValue and <= int.MaxValue => (int)l.Value,
            NodeValue<decimal> m when m.Value == decimal.Truncate(m.Value) && m.Value is >= int.MinValue and <= int.MaxValue => (int)m.Value,
            NodeValue<string> s when int.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw TypeError(key, "an integer"),
        };
    }

    /// <summary>
    /// Reads a string or a list of strings, or returns an empty list when the option is absent.
    /// </summary>
    public IReadOnlyList<string> GetStrings(string key)
    {
        if (!_definition.With.TryGetItem(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            NodeValue<string> s => [s.Value],
            NodeArray array when array.All(x => x is NodeValue<string>) => [.. array.Select(x => ((NodeValue<string>)x!).Value)],
            _ => throw TypeError(key, "a string or a list of strings"),
        };
    }

    /// <summary>
    /// Reads a glob or a list of globs, or returns <paramref name="defaultValue"/> when the option is absent.
    /// </summary>
    public GlobSet? GetGlobs(string key, GlobSet? defaultValue = null)
    {
        if (!_definition.With.TryGetItem(key, out var value) || value is null)
        {
            return defaultValue;
        }

        var patterns = value switch
        {
            NodeValue<string> s => [s.Value],
            NodeArray array when array.All(x => x is NodeValue<string>) => array.Select(x => ((NodeValue<string>)x!).Value).ToArray(),
            _ => throw TypeError(key, "a glob or a list of globs"),
        };

        try
        {
            return GlobSet.Parse(patterns);
        }
        catch (GlobFormatException e)
        {
            throw new WorkflowValidationException($"invalid pattern '{patterns.First(p => !TryParse(p))}': {e.Message}", _definition.Location);
        }

        static bool TryParse(string pattern)
        {
            try
            {
                Glob.Parse(pattern);
                return true;
            }
            catch (GlobFormatException)
            {
                return false;
            }
        }
    }

    private WorkflowValidationException TypeError(string key, string expected)
    {
        return new WorkflowValidationException($"option '{key}' of step '{_definition.Name}' must be {expected}", _definition.Location);
    }
}
