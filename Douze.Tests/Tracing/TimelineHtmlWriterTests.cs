using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Douze.Tracing;
using Xunit;

namespace Douze.Tests.Tracing;

public class TimelineHtmlWriterTests
{
    private static string Write(TraceSession session)
    {
        using var stream = new MemoryStream();
        TimelineHtmlWriter.Write(session, stream, [new("site", "</script><script>alert(1)</script>")]);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static TraceSession Session(int units)
    {
        var session = new TraceSession();
        var ms = Stopwatch.Frequency / 1000;

        session.Add(new TraceEvent("build", "build", null, 0, 2000 * ms, 1, []));
        session.Add(new TraceEvent("markdown", "step", null, 10 * ms, 1000 * ms, 1, [new("files", units.ToString())]));

        for (var i = 0; i < units; i++)
        {
            session.Add(new TraceEvent("markdown", "unit", $"/posts/2024/post-{i}.md", (10 + i) * ms, 3 * ms, 1, []));
        }

        return session;
    }

    [Fact]
    public void MarkerIsReplacedWithTheTrace()
    {
        var html = Write(Session(2));

        Assert.DoesNotContain("__DOUZE_TRACE__", html);
        Assert.Contains("const trace = {\"traceEvents\":[", html);
        Assert.Contains("/posts/2024/post-1.md", html);

        // The data cannot close the script element.
        Assert.Single(Regex.Matches(html, "</script>"));
    }

    [Fact]
    public void NoExternalReferences()
    {
        var html = Write(Session(2));

        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotMatch(@"<(script|img|iframe)[^>]+src=|<link\b|@import|url\(", html);
    }

    [Fact]
    public void StaysSmall()
    {
        var page = Write(new TraceSession());
        Assert.True(page.Length < 20 * 1024, $"the page is {page.Length} bytes");

        // With 1,000 unit spans (--trace-units), the page grows by the Chrome trace events only.
        var traced = Write(Session(1000));
        var perSpan = (traced.Length - page.Length) / 1000.0;
        Assert.True(perSpan < 150, $"{perSpan} bytes per span");
    }
}
