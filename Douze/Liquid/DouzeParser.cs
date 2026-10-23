using Fluid;
using Fluid.Ast;
using Parlot.Fluent;

namespace Douze.Liquid;

/// <summary>
/// The Liquid parser of a build: Fluid's, with the parsers Douze's tags need, which <see cref="FluidParser"/> keeps
/// protected (the argument list of filters, the filter expression).
/// </summary>
internal sealed class DouzeParser : FluidParser
{
    public DouzeParser(FluidParserOptions options) : base(options)
    {
        // Nothing to parse: only the position of the tag.
        TagPosition = Parsers.Always<object?>().Then(static (context, start, _, _) => new TagPosition(context.Scanner.Buffer, start));

        // Positional values and name: value pairs separated by commas, or nothing.
        TagArguments = ArgumentsList
            .Else([])
            .Then(static (context, start, _, arguments) => new TagArguments(arguments ?? [], new TagPosition(context.Scanner.Buffer, start)));

        TagExpression = FilterExpression.Then(static (context, start, _, expression) => new TagExpression(expression, new TagPosition(context.Scanner.Buffer, start)));
    }

    /// <summary>
    /// <c>{% tag %}</c>: the position of the tag.
    /// </summary>
    public Parser<TagPosition> TagPosition { get; }

    /// <summary>
    /// <c>{% tag "a", b, name: c %}</c>: positional and named arguments, any Liquid value or variable, and the
    /// position of the tag.
    /// </summary>
    public Parser<TagArguments> TagArguments { get; }

    /// <summary>
    /// <c>{% tag value | filter: x %}</c>: an expression with filters, and the position of the tag.
    /// </summary>
    public Parser<TagExpression> TagExpression { get; }

    /// <summary>
    /// The names of the shortcode tags registered on this parser. Watch mode scans the shortcodes again on the parser of
    /// the previous build, which removes these first: they are not built-in tags, and a deleted shortcode is no tag.
    /// </summary>
    public HashSet<string> ShortcodeTags { get; } = new(StringComparer.Ordinal);
}

internal sealed record TagArguments(IReadOnlyList<FilterArgument> Arguments, TagPosition Position);

internal sealed record TagExpression(Expression Expression, TagPosition Position);

/// <summary>
/// Where a tag is in the source of its template: the offset of its <c>{%</c>. The line, column and the text before the
/// tag on its line are computed when they are asked for (errors, include indentation), not for every tag parsed.
/// </summary>
internal sealed class TagPosition
{
    private readonly string _source;
    private readonly int _offset;

    public TagPosition(string source, int offset)
    {
        _source = source;

        // The parser starts after the tag name: go back to the tag's {%.
        var start = offset;

        while (start > 0 && !(source[start - 1] == '{' && start < source.Length && source[start] == '%'))
        {
            start--;
        }

        _offset = start > 0 ? start - 1 : offset;
    }

    /// <summary>
    /// The 1-based line of the tag.
    /// </summary>
    public int Line => _source.AsSpan(0, _offset).Count('\n') + 1;

    /// <summary>
    /// The 1-based column of the tag.
    /// </summary>
    public int Column => _offset - LineStart + 1;

    private int LineStart => _offset == 0 ? 0 : _source.LastIndexOf('\n', _offset - 1) + 1;

    /// <summary>
    /// The text before the tag on its line.
    /// </summary>
    public ReadOnlySpan<char> TextBefore => _source.AsSpan(LineStart, _offset - LineStart);

    /// <summary>
    /// The prefix that keeps a line in the container of the tag (list items, block quotes): the indentation and the
    /// <c>&gt;</c> before the tag, with list markers (<c>-</c>, <c>*</c>, <c>+</c>, <c>1.</c>, <c>1)</c>) turned into
    /// spaces. It stops at the first other text: a tag in the middle of a paragraph keeps the paragraph's container
    /// only.
    /// </summary>
    public string ContainerPrefix
    {
        get
        {
            var before = TextBefore;
            var prefix = new System.Text.StringBuilder();
            var i = 0;

            while (i < before.Length)
            {
                var c = before[i];

                if (c is ' ' or '\t' or '>')
                {
                    prefix.Append(c);
                    i++;
                    continue;
                }

                var marker = MarkerLength(before[i..]);

                if (marker == 0)
                {
                    break;
                }

                prefix.Append(' ', marker);
                i += marker;
            }

            return prefix.ToString();
        }
    }

    /// <summary>
    /// The length of a list marker and the space after it at the start of <paramref name="text"/>, or 0.
    /// </summary>
    private static int MarkerLength(ReadOnlySpan<char> text)
    {
        if (text.Length >= 2 && text[0] is '-' or '*' or '+' && text[1] is ' ' or '\t')
        {
            return 2;
        }

        var digits = 0;

        while (digits < text.Length && digits < 9 && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        return digits > 0 && text.Length > digits + 1 && text[digits] is '.' or ')' && text[digits + 1] is ' ' or '\t' ? digits + 2 : 0;
    }
}
