using System.Text;
using Douze.FileSystems;
using Douze.Liquid;
using Fluid;

namespace Douze.Workflow;

// Exception: The exception, only for unexpected failures: views print its stack trace with <c>--verbose</c>.
internal sealed record BuildError(string Step, FPath? File, string Message, SourceLocation? Location, Exception? Exception)
{
    /// <summary>
    /// <c>error: &lt;step&gt;: &lt;file&gt;(&lt;line&gt;,&lt;col&gt;): &lt;message&gt;</c>, without the parts that are null.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder("error: ");

        if (!string.IsNullOrEmpty(Step))
        {
            builder.Append(Step).Append(": ");
        }

        var file = File?.FullName ?? Location?.File;

        if (file != null)
        {
            builder.Append(file);

            if (Location is { } location)
            {
                builder.Append('(').Append(location.Line).Append(',').Append(location.Column).Append(')');
            }

            builder.Append(": ");
        }

        builder.Append(Message);

        return builder.ToString();
    }

    /// <summary>
    /// Converts an exception thrown by a step for <paramref name="file"/>. Douze, Liquid and YAML errors are reported with
    /// their message only; other exceptions keep the exception so its stack trace can be shown.
    /// </summary>
    public static BuildError FromException(string step, FPath? file, Exception exception)
    {
        switch (exception)
        {
            case ParseException parse:
                SourceLocation? location = parse.Position is { } position
                    ? new SourceLocation(file?.FullName ?? "", position.Line, position.Column)
                    : null;
                return new BuildError(step, file, parse.Message, location, null);

            // A page body: the position is in the file itself. A layout or a permalink: the message names the template.
            case TemplateParseException template when template.Line is { } line && (file == null || template.Path == file.Value.FullName):
                return new BuildError(step, file ?? template.Path, template.Reason, new SourceLocation(template.Path, line, template.Column ?? 0), null);

            // A tag of the page's own template (or of a template that is not known): the position is in the file.
            case TemplateRenderException render when render.Path == null || file == null || render.Path == file.Value.FullName:
                return new BuildError(step, file ?? (render.Path is { } path ? new FPath(path) : (FPath?)null), render.Reason, new SourceLocation(render.Path ?? file?.FullName ?? "", render.Line, render.Column), null);

            case TemplateParseException or TemplateRenderException or DouzeException or WorkflowValidationException or SharpYaml.YamlException:
                return new BuildError(step, file, exception.Message, null, null);

            default:
                return new BuildError(step, file, exception.Message, null, exception);
        }
    }
}
