namespace Douze.Liquid;

/// <summary>
/// An error raised by a tag during a render (<c>{% error %}</c>), at a position of a template. The message is
/// <c>&lt;path&gt;(&lt;line&gt;,&lt;col&gt;): &lt;reason&gt;</c>, or the reason alone when the template is not known.
/// </summary>
internal sealed class TemplateRenderException : Exception
{
    public TemplateRenderException(string? path, int line, int column, string reason)
        : base(path == null ? reason : $"{path}({line},{column}): {reason}")
    {
        Path = path;
        Line = line;
        Column = column;
        Reason = reason;
    }

    /// <summary>
    /// The path of the template the tag is in, when the render knows it.
    /// </summary>
    public string? Path { get; }

    public int Line { get; }

    public int Column { get; }

    public string Reason { get; }
}
