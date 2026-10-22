using Douze.Twoslash;
using Xunit;

namespace Douze.Tests.Twoslash;

public class IdentifierScannerTests
{
    private static string[] Words(string code, bool jsx = false) =>
        [.. IdentifierScanner.Scan(code, jsx).Select(x => (x.AfterDot ? "." : "") + code.Substring(x.Start, x.Length))];

    [Fact]
    public void SkipsCommentsStringsAndNumbers()
    {
        Assert.Equal(["const", "a", "b", ".c"], Words("const a = 'x y' + \"z\" + 1.5e+3 + 0xFF; // comment\n/* block */ b?.c;"));
    }

    [Fact]
    public void TemplateSubstitutionsAreCode()
    {
        Assert.Equal(["x", "y", ".z"], Words("x = `text ${y.z} more ${'}'} end`;"));
    }

    [Fact]
    public void RegularExpressionsAndDivisions()
    {
        Assert.Equal(["const", "r", "a", "b"], Words("const r = /it's [/] a/gi; a / b / 2;"));
        Assert.Equal(["return"], Words("return /x'y/.source;").Take(1));
    }

    [Fact]
    public void PrivateNamesAreNotIdentifiers()
    {
        Assert.Equal(["class", "A", "x", "this"], Words("class A { #x = 1; x() { this.#x; } }"));
    }

    [Fact]
    public void KeywordsAfterADotAreProperties()
    {
        Assert.Equal(["a", ".default", ".new"], Words("a.default.new"));
    }

    [Fact]
    public void Jsx()
    {
        const string code = "const e = <Foo.Bar value={x} label=\"it's\">it's {y} text<b>z</b></Foo.Bar>;";
        Assert.Equal(["const", "e", "Foo", ".Bar", "value", "x", "label", "y", "b", "b", "Foo", ".Bar"], Words(code, jsx: true));
    }

    [Fact]
    public void GenericArrowFunctionsInTsxAreNotJsx()
    {
        Assert.Equal(["const", "f", "T", "x", "T", "x"], Words("const f = <T,>(x: T) => x;", jsx: true));
    }

    [Fact]
    public void UnicodeIdentifiers()
    {
        Assert.Equal(["const", "café", "π"], Words("const café = π;"));
    }
}
