using System.Globalization;

namespace Douze.Bench;

internal sealed class BenchException(string message) : Exception(message);

/// <summary>
/// <c>--name value</c> options; everything after <c>--</c> is kept as is.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Rest { get; private set; } = [];

    public static Arguments Parse(ReadOnlySpan<string> args)
    {
        var result = new Arguments();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                result.Rest = args[(i + 1)..].ToArray();
                break;
            }

            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new BenchException($"expected '--<name> <value>', got '{args[i]}'");
            }

            result._values[args[i][2..]] = args[++i];
        }

        return result;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Required(string name) => Get(name) ?? throw new BenchException($"--{name} is required");

    public int Int(string name, int defaultValue)
    {
        if (Get(name) is not { } value)
        {
            return defaultValue;
        }

        return int.TryParse(value, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new BenchException($"--{name} must be a positive integer, not '{value}'");
    }
}
