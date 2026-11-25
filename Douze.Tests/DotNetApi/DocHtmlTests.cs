using System.Xml.Linq;
using Douze.DotNetApi;
using Xunit;

namespace Douze.Tests.DotNetApi;

public sealed class DocHtmlTests
{
    [Theory]
    // A paragraph.
    [InlineData("<para>One <c>two</c>.</para>", "<p>One <code>two</code>.</p>")]
    // The blocks of a paragraph are between paragraphs: HTML has no <pre> or <ul> in a <p>.
    [InlineData("<para>Call it:<code>Run();</code>Then wait.</para>", "<p>Call it:</p><pre data-lang=\"csharp\"><code class=\"language-csharp\">Run();</code></pre><p>Then wait.</p>")]
    [InlineData("<para><code>Run();</code></para>", "<pre data-lang=\"csharp\"><code class=\"language-csharp\">Run();</code></pre>")]
    [InlineData("<para>Either <list><item><description>one</description></item></list> </para>", "<p>Either</p><ul><li>one</li></ul>")]
    [InlineData("<para>Outer <para>inner</para> end</para>", "<p>Outer</p><p>inner</p><p>end</p>")]
    public void Paragraphs(string xml, string html)
    {
        var doc = new DocHtml(cref => (cref, null));
        Assert.Equal(html, doc.Render(XElement.Parse("<summary>" + xml + "</summary>", LoadOptions.PreserveWhitespace)));
    }
}
