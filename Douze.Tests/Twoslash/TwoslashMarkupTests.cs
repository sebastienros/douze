using Douze.CodeHover;
using Douze.Twoslash;
using Xunit;

namespace Douze.Tests.Twoslash;

public class TwoslashMarkupTests
{
    private static readonly string[] Tags = ["log"];

    private static TwoslashMarkup Parse(string code, string language = "ts") => TwoslashMarkup.Parse(code, language, Tags, new HandbookOptions());

    [Fact]
    public void Flags()
    {
        var markup = Parse("// @strict: false\n// @target: ES2015\n// @lib: es2015, dom\n// @noErrors\n// @errors: 2322 2345\n// @log: hello\n// @unknownFlag\nlet x = 1;");

        Assert.Equal("false", markup.CompilerOptions["strict"]);
        Assert.Equal("\"ES2015\"", markup.CompilerOptions["target"]);
        Assert.Equal("[\"es2015\",\"dom\"]", markup.CompilerOptions["lib"]);
        Assert.True(markup.Handbook.NoErrors);
        Assert.Equal([2322, 2345], markup.Handbook.Errors);
        Assert.Equal(["unknownFlag"], markup.UnknownFlags);

        var tag = Assert.Single(markup.Tags);
        Assert.Equal(("log", "hello"), (tag.Name, tag.Text));

        // Every notation line is removed with its line end.
        Assert.Equal("let x = 1;", Remove(markup));
    }

    [Fact]
    public void CompilerOptionNamesAreCaseInsensitive()
    {
        var markup = Parse("// @NOIMPLICITANY: true\n// @moduleresolution: bundler\nx;");

        Assert.Equal("true", markup.CompilerOptions["noImplicitAny"]);
        Assert.Equal("\"bundler\"", markup.CompilerOptions["moduleResolution"]);
    }

    [Fact]
    public void NoErrorsWithCodes()
    {
        var markup = Parse("// @noErrors: 2322 2588\nx;");

        Assert.False(markup.Handbook.NoErrors);
        Assert.Equal([2322, 2588], markup.Handbook.IgnoredErrors);
    }

    [Fact]
    public void InvalidErrorCode()
    {
        var e = Assert.Throws<TwoslashException>(() => Parse("// @errors: 2322 abc\nx;"));
        Assert.Contains("'abc'", e.Message);
    }

    [Fact]
    public void Cuts()
    {
        Assert.Equal("b\n", Remove(Parse("a\n// ---cut---\nb\n")));
        Assert.Equal("b\n", Remove(Parse("a\n// ---cut-before---\nb\n")));
        Assert.Equal("a\n", Remove(Parse("a\n// ---cut-after---\nb\n")));
        Assert.Equal("a\nc\n", Remove(Parse("a\n// ---cut-start---\nb\n// ---cut-end---\nc\n")));

        Assert.Throws<TwoslashException>(() => Parse("a\n// ---cut-start---\nb\n"));
        Assert.Throws<TwoslashException>(() => Parse("a\n// ---cut-end---\nb\n"));
    }

    [Fact]
    public void Queries()
    {
        const string code = "const a = 1;\n//    ^?\nconst b = a;\n//    ^|\nconst c = 'x';\n//    ^^^ the c\n";
        var markup = Parse(code);

        // About the line above: `a` of line 0, `b` of line 2, `c` of line 4, at the marker's column.
        Assert.Equal([code.IndexOf("a = 1", StringComparison.Ordinal)], markup.Queries);
        Assert.Equal([code.IndexOf("b = a", StringComparison.Ordinal)], markup.Completions);
        var highlight = Assert.Single(markup.Highlights);
        Assert.Equal((code.IndexOf("c = 'x'", StringComparison.Ordinal), 3, "the c"), (highlight.Start, highlight.End - highlight.Start, highlight.Text));

        Assert.Equal("const a = 1;\nconst b = a;\nconst c = 'x';\n", Remove(markup));
    }

    [Fact]
    public void ConsecutiveQueriesSkipMarkerLines()
    {
        const string code = "const value = { a: 1 };\n//    ^?\n//        ^?\n";
        var markup = Parse(code);

        // The second marker is about the line before the first marker, not the first marker's line.
        Assert.Equal([6, 10], markup.Queries);
    }

    [Fact]
    public void Files()
    {
        const string code = "// @filename: a.ts\nexport const a = 1;\n// @filename: b.ts\nimport { a } from './a';\n";
        var markup = Parse(code);

        Assert.Equal(["a.ts", "b.ts"], markup.Files.Select(x => x.Name));
        Assert.Equal([0, code.IndexOf("// @filename: b.ts", StringComparison.Ordinal)], markup.Files.Select(x => x.Offset));

        // The filename lines stay in the code, as twoslash leaves them.
        Assert.Equal(code, Remove(markup));
    }

    [Fact]
    public void CodeBeforeTheFirstFileIsTheDefaultFile()
    {
        var markup = Parse("const x = 1;\n// @filename: other.tsx\nexport {};\n", "typescript");

        Assert.Equal(["index.ts", "other.tsx"], markup.Files.Select(x => x.Name));
        Assert.Equal("ts", markup.Extension);
    }

    [Fact]
    public void Positions()
    {
        var positions = new PositionConverter("ab\ncd\n");

        Assert.Equal((0, 1), positions.ToPosition(1));
        Assert.Equal((1, 0), positions.ToPosition(3));
        // The end of the code is past the last (empty) line, as twoslash counts.
        Assert.Equal((3, 0), positions.ToPosition(6));
        Assert.Equal(4, positions.ToIndex(1, 1));
    }

    [Fact]
    public void UnknownLanguage()
    {
        Assert.Throws<TwoslashException>(() => Parse("x", "python"));
    }

    private static string Remove(TwoslashMarkup markup)
    {
        var code = markup.Code;

        foreach (var (start, end) in markup.Removals.OrderByDescending(x => x.Start))
        {
            code = code[..start] + code[Math.Min(end, code.Length)..];
        }

        return code;
    }
}
