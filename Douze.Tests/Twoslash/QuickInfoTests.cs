using Douze.Twoslash;
using Xunit;

namespace Douze.Tests.Twoslash;

public class QuickInfoTests
{
    [Fact]
    public void SignatureOnly()
    {
        var info = QuickInfo.Parse("```typescript\nconst x: number\n```\n");

        Assert.Equal(new QuickInfo("const x: number", null, null), info);
    }

    [Fact]
    public void DocumentationAndTags()
    {
        var info = QuickInfo.Parse("```typescript\nfunction f(a: number): string\n```\nDocs for f.\n\n*@param* `a` — the a\n\n*@param* `b` - dashed\n\n*@returns* — stuff\n\n*@deprecated*");

        Assert.NotNull(info);
        Assert.Equal("function f(a: number): string", info.Text);
        Assert.Equal("Docs for f.", info.Docs);
        Assert.Equal([("param", "a the a"), ("param", "b - dashed"), ("returns", "stuff"), ("deprecated", (string?)null)], info.Tags);
    }

    [Fact]
    public void Example()
    {
        var info = QuickInfo.Parse("```typescript\nfunction f(): void\n```\n\n\n*@example*\n```tsx\nf();\n```\n");

        Assert.NotNull(info);
        Assert.Null(info.Docs);
        Assert.Equal([("example", "f();")], info.Tags);
    }

    [Fact]
    public void SignatureWithBackticks()
    {
        var info = QuickInfo.Parse("````typescript\ntype T = `a```b`\n````\nDocs");

        Assert.NotNull(info);
        Assert.Equal("type T = `a```b`", info.Text);
        Assert.Equal("Docs", info.Docs);
    }

    [Fact]
    public void DeclarationLinks()
    {
        var info = QuickInfo.Parse("```typescript\nconst x: number\n```\nSee [Foo](file:///a.ts#1,1-1,4) here");

        Assert.NotNull(info);
        Assert.Equal("See \n{@link \nFoo\n}\n here", info.Docs);
    }

    [Fact]
    public void Empty()
    {
        Assert.Null(QuickInfo.Parse(""));
        Assert.Null(QuickInfo.Parse("no code block"));
    }
}
