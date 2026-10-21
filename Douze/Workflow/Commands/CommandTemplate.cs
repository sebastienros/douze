using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Douze.FileSystems;
using Douze.Liquid;
using Fluid;
using Fluid.Ast;
using Fluid.Values;

namespace Douze.Workflow.Commands;

/// <summary>
/// The <c>run</c> line and the <c>env</c> values of a command step, parsed once and rendered per unit with
/// <c>inputs</c>, <c>outputs</c>, <c>site</c>, <c>step</c>, <c>cwd</c> and, in filter mode, <c>file</c>
/// (<c>path</c>, <c>name</c>, <c>dir</c>, <c>ext</c>).
/// </summary>
/// <remarks>
/// In the <c>run</c> line, a value written directly (<c>{{ outputs }}</c>) is quoted for the shell when it contains
/// anything else than letters, digits and <c>_-./:@+=,</c>; a value that goes through a filter is written as the
/// filter returns it. Environment values are never quoted.
/// </remarks>
internal sealed class CommandTemplate
{
    private static readonly FluidParser Parser = LiquidOptions.CreateParser();

    private readonly IFluidTemplate _run;
    private readonly KeyValuePair<string, IFluidTemplate>[] _env;
    private readonly QuoteStyle _style;

    private CommandTemplate(IFluidTemplate run, KeyValuePair<string, IFluidTemplate>[] env, QuoteStyle style, IReadOnlySet<string> names)
    {
        _run = run;
        _env = env;
        _style = style;
        UsesInputs = names.Contains("inputs");
        UsesOutputs = names.Contains("outputs");
    }

    /// <summary>
    /// Whether the <c>run</c> line or an <c>env</c> value references <c>inputs</c>: a batch step then writes its inputs to disk.
    /// </summary>
    public bool UsesInputs { get; }

    /// <summary>
    /// Whether the <c>run</c> line or an <c>env</c> value references <c>outputs</c>: a batch step then reads its outputs back.
    /// </summary>
    public bool UsesOutputs { get; }

    /// <exception cref="WorkflowValidationException">A template is invalid, or uses a value its mode does not provide.</exception>
    public static CommandTemplate Parse(StepDefinition definition)
    {
        var errors = new List<WorkflowValidationError>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        var run = ParseOne(definition.Run ?? "", "'run'");
        var env = definition.Env
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => KeyValuePair.Create(x.Key, ParseOne(x.Value, $"'env.{x.Key}'")))
            .ToArray();

        if (definition.Mode != CommandMode.Batch)
        {
            foreach (var name in (ReadOnlySpan<string>)["inputs", "outputs"])
            {
                if (names.Contains(name))
                {
                    errors.Add(new($"'{{{{ {name} }}}}' is only valid with 'mode: batch' (step '{definition.Name}')", definition.Location));
                }
            }
        }

        if (definition.Mode != CommandMode.Filter && names.Contains("file"))
        {
            errors.Add(new($"'{{{{ file }}}}' is only valid with 'mode: filter' (step '{definition.Name}')", definition.Location));
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        return new CommandTemplate(run, env, QuoteStyleFor(definition.Shell), names);

        IFluidTemplate ParseOne(string text, string what)
        {
            if (!Parser.TryParse(text, out var template, out var error))
            {
                errors.Add(new($"invalid template in {what} of step '{definition.Name}': {FirstLine(error)}", definition.Location));
                return new Fluid.Parser.FluidTemplate();
            }

            new NameCollector(names).VisitTemplate(template);
            return template;
        }

        static string FirstLine(string error)
        {
            var end = error.IndexOf('\n');
            return (end >= 0 ? error[..end] : error).TrimEnd();
        }
    }

    /// <summary>
    /// Renders the command line, with the values written directly quoted for the shell (or for the splitter of
    /// <c>shell: false</c>).
    /// </summary>
    public ValueTask<string> RenderRunAsync(CommandModel model, TemplateOptions options, CancellationToken ct)
    {
        return RenderAsync(_run, model, _style, options, ct);
    }

    /// <summary>
    /// Renders the <c>env</c> values, sorted by name, without quoting.
    /// </summary>
    public async ValueTask<IReadOnlyList<KeyValuePair<string, string>>> RenderEnvAsync(CommandModel model, TemplateOptions options, CancellationToken ct)
    {
        if (_env.Length == 0)
        {
            return [];
        }

        var result = new KeyValuePair<string, string>[_env.Length];

        for (var i = 0; i < _env.Length; i++)
        {
            result[i] = KeyValuePair.Create(_env[i].Key, await RenderAsync(_env[i].Value, model, QuoteStyle.None, options, ct));
        }

        return result;
    }

    private static async ValueTask<string> RenderAsync(IFluidTemplate template, CommandModel model, QuoteStyle style, TemplateOptions options, CancellationToken ct)
    {
        var context = new TemplateContext(options) { CancellationToken = ct };

        context.SetValue("step", new ArgumentValue(model.Step, style));
        context.SetValue("cwd", new ArgumentValue(model.WorkingDirectory, style));
        Set("site", model.Site);
        Set("inputs", model.Inputs);
        Set("outputs", model.Outputs);

        if (model.File is { } file)
        {
            context.SetValue("file", new ObjectArgumentValue(new Dictionary<string, FluidValue>(StringComparer.Ordinal)
            {
                ["path"] = new ArgumentValue(file.FullName, style),
                ["dir"] = new ArgumentValue(file.GetDirectory().FullName, style),
                ["name"] = new ArgumentValue(Path.GetFileName(file.FullName), style),
                ["ext"] = new ArgumentValue(file.GetExtensionWithDot(), style),
            }));
        }

        return await template.RenderAsync(context, NullEncoder.Default);

        void Set(string name, string? value)
        {
            if (value != null)
            {
                context.SetValue(name, new ArgumentValue(value, style));
            }
        }
    }

    public static QuoteStyle QuoteStyleFor(bool shell)
    {
        if (!shell)
        {
            return QuoteStyle.Split;
        }

        return OperatingSystem.IsWindows() ? QuoteStyle.Cmd : QuoteStyle.Posix;
    }

    /// <summary>
    /// Quotes <paramref name="value"/> for <paramref name="style"/> when it contains a character that needs it.
    /// </summary>
    public static string Quote(string value, QuoteStyle style)
    {
        if (style == QuoteStyle.None || (value.Length > 0 && value.All(c => IsSafe(c, style))))
        {
            return value;
        }

        switch (style)
        {
            case QuoteStyle.Posix:
                // 'it'\''s': the only character a single quoted string cannot contain is the single quote.
                return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

            case QuoteStyle.Cmd:
                // Windows paths cannot contain double quotes.
                return "\"" + value + "\"";

            default:
                // The splitter of 'shell: false' reads '...' literally, and "..." with \" as a quote.
                return value.Contains('\'')
                    ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                    : "'" + value + "'";
        }

        static bool IsSafe(char c, QuoteStyle style)
        {
            return char.IsAsciiLetterOrDigit(c)
                || c is '_' or '-' or '.' or '/' or ':' or '@' or '+' or '=' or ','
                // A backslash is a path separator for cmd and for the splitter on Windows, an escape for the POSIX shell.
                || c == '\\' && (style == QuoteStyle.Cmd || style == QuoteStyle.Split && OperatingSystem.IsWindows());
        }
    }

    /// <summary>
    /// Collects the root names a template reads.
    /// </summary>
    private sealed class NameCollector(HashSet<string> names) : AstVisitor
    {
        protected override Expression VisitMemberExpression(MemberExpression memberExpression)
        {
            if (memberExpression.Segments.Count > 0 && memberExpression.Segments[0] is IdentifierSegment identifier)
            {
                names.Add(identifier.Identifier);
            }

            return base.VisitMemberExpression(memberExpression);
        }
    }

    /// <summary>
    /// A string that quotes itself when written directly in the output, and is the raw string for filters.
    /// </summary>
    private sealed class ArgumentValue(string value, QuoteStyle style) : FluidValue
    {
        public string Raw => value;

        public override FluidValues Type => FluidValues.String;

        public override bool Equals(FluidValue other) => other is ArgumentValue argument && argument.Raw == value;

        [Obsolete("Use the TemplateContext overload.")]

        public override bool ToBooleanValue() => true;

        [Obsolete("Use the TemplateContext overload.")]

        public override decimal ToNumberValue() => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0;

        [Obsolete("Use the TemplateContext overload.")]

        public override string ToStringValue() => value;

        [Obsolete("Use the TemplateContext overload.")]

        public override object ToObjectValue() => value;

        public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo)
        {
            output.Write(Quote(value, style));
            return default;
        }

        public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

        public override int GetHashCode() => value.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>file</c> object.
    /// </summary>
    private sealed class ObjectArgumentValue(Dictionary<string, FluidValue> members) : FluidValue
    {
        public override FluidValues Type => FluidValues.Dictionary;

        public override ValueTask<FluidValue> GetValueAsync(string name, TemplateContext context)
        {
            return new(members.TryGetValue(name, out var value) ? value : NilValue.Instance);
        }

        public override bool Equals(FluidValue other) => ReferenceEquals(this, other);

        [Obsolete("Use the TemplateContext overload.")]

        public override bool ToBooleanValue() => true;

        [Obsolete("Use the TemplateContext overload.")]

        public override decimal ToNumberValue() => 0;

        [Obsolete("Use the TemplateContext overload.")]

        public override string ToStringValue() => "";

        [Obsolete("Use the TemplateContext overload.")]

        public override object ToObjectValue() => members;

        public override ValueTask WriteToAsync(IFluidOutput output, TextEncoder encoder, CultureInfo cultureInfo) => default;

        public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

        public override int GetHashCode() => members.GetHashCode();
    }
}

internal enum QuoteStyle
{
    /// <summary>Environment values: never quoted.</summary>
    None,

    /// <summary><c>/bin/sh -c</c>: single quotes.</summary>
    Posix,

    /// <summary><c>cmd.exe /d /s /c</c>: double quotes.</summary>
    Cmd,

    /// <summary><c>shell: false</c>: the quote rules of <see cref="ProcessRunner.Split"/>.</summary>
    Split,
}

/// <summary>
/// The values a command template is rendered with. Paths are absolute disk paths, except <see cref="File"/>, the
/// virtual path of the file in filter mode.
/// </summary>
internal sealed record CommandModel(string Step, string WorkingDirectory, string? Site, string? Inputs = null, string? Outputs = null, FPath? File = null);
