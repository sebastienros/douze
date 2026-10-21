using System.Text;

namespace Douze.Tracing;

/// <summary>
/// Writes a self-contained <c>timeline.html</c>: the embedded page with the Chrome trace of the build in place of its
/// marker.
/// </summary>
internal static class TimelineHtmlWriter
{
    public const string Marker = "/*__DOUZE_TRACE__*/null";

    private static readonly Lazy<string> Template = new(LoadTemplate);

    public static void Write(TraceSession session, Stream stream, IEnumerable<KeyValuePair<string, string>>? otherData = null)
    {
        var template = Template.Value;
        var index = template.IndexOf(Marker, StringComparison.Ordinal);

        stream.Write(Encoding.UTF8.GetBytes(template[..index]));
        ChromeTraceWriter.Write(session, stream, otherData);
        stream.Write(Encoding.UTF8.GetBytes(template[(index + Marker.Length)..]));
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(TimelineHtmlWriter).Assembly.GetManifestResourceStream("timeline.html")
            ?? throw new InvalidOperationException("The timeline.html resource is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var template = reader.ReadToEnd();

        if (!template.Contains(Marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"timeline.html does not contain {Marker}");
        }

        return template;
    }
}
