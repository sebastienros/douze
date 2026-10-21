namespace Douze.Liquid;

/// <summary>
/// A template that does not parse. The message is <c>&lt;path&gt;(&lt;line&gt;,&lt;col&gt;): &lt;reason&gt;</c>.
/// </summary>
internal sealed class TemplateParseException : Exception
{
    public TemplateParseException(string path, int? line, int? column, string reason)
        : base($"{path}{(line is { } l ? $"({l},{column})" : "")}: {reason}")
    {
        Path = path;
        Line = line;
        Column = column;
        Reason = reason;
    }

    /// <summary>
    /// The path of the template, or a description such as "permalink of '/a.md'".
    /// </summary>
    public string Path { get; }

    public int? Line { get; }

    public int? Column { get; }

    public string Reason { get; }
}
