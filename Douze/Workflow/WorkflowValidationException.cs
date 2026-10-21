namespace Douze.Workflow;

internal readonly record struct WorkflowValidationError(string Message, SourceLocation? Location)
{
    /// <summary>
    /// <c>douze.yml(12,5): error: &lt;message&gt;</c>, or <c>error: &lt;message&gt;</c> without a location.
    /// </summary>
    public override string ToString() => Location is { } location ? $"{location}: error: {Message}" : $"error: {Message}";
}

/// <summary>
/// One or more errors in a workflow definition. Every error is collected before the exception is thrown.
/// </summary>
internal sealed class WorkflowValidationException : Exception
{
    public WorkflowValidationException(string message, SourceLocation? location = null)
        : this([new WorkflowValidationError(message, location)])
    {
    }

    public WorkflowValidationException(IReadOnlyList<WorkflowValidationError> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        if (errors.Count == 0)
        {
            throw new ArgumentException("At least one error is required.", nameof(errors));
        }

        Errors = errors;
    }

    public IReadOnlyList<WorkflowValidationError> Errors { get; }

    /// <summary>
    /// Returns <c>" (did you mean 'x'?)"</c> for the closest candidate within a Levenshtein distance of 2, or an empty string.
    /// </summary>
    public static string Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates.Order(StringComparer.Ordinal))
        {
            var distance = Levenshtein(name, candidate);

            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best != null && bestDistance <= 2 ? $"; did you mean '{best}'?" : "";
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
