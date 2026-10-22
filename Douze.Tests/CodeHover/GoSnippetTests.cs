using Douze.CodeHover.Go;
using Xunit;

namespace Douze.Tests.CodeHover;

public class GoSnippetTests
{
    [Fact]
    public void FilesStayAsTheyAre()
    {
        const string code = "// Package main runs.\npackage main\n\nfunc main() {}";
        var snippet = GoSnippet.Wrap(code);

        Assert.Equal(code, snippet.Document);
        Assert.Equal(5, snippet.ToCode(snippet.ToDocument(5)));
    }

    [Fact]
    public void DeclarationsGetAPackageClause()
    {
        var snippet = GoSnippet.Wrap("import \"fmt\"\n\n// Greet greets.\nfunc Greet() { fmt.Println(\"hi\") }\n\ntype T struct{}");

        Assert.Equal("package main\nimport \"fmt\"\n\n// Greet greets.\nfunc Greet() { fmt.Println(\"hi\") }\n\ntype T struct{}", snippet.Document);

        // Offsets map both ways; the package clause maps to nothing, or to the code after it.
        var greet = snippet.Code.IndexOf("Greet()", StringComparison.Ordinal);
        Assert.Equal(greet + GoSnippet.PackageClause.Length, snippet.ToDocument(greet));
        Assert.Equal(greet, snippet.ToCode(snippet.ToDocument(greet)));
        Assert.Equal(-1, snippet.ToCode(3));
        Assert.Equal(0, snippet.ToCode(3, clamp: true));
    }

    [Fact]
    public void StatementsGoInAFunction()
    {
        var snippet = GoSnippet.Wrap("import (\n\t\"fmt\"\n)\n\ntype point struct{ x int }\n\n// A point.\np := point{\n\tx: 1,\n}\nfmt.Println(p.x)");

        Assert.Equal("package main\nimport (\n\t\"fmt\"\n)\n\ntype point struct{ x int }\nfunc _() {\n\n// A point.\np := point{\n\tx: 1,\n}\nfmt.Println(p.x)\n}\n", snippet.Document);

        var x = snippet.Code.LastIndexOf("x)", StringComparison.Ordinal);
        Assert.Equal('x', snippet.Document[snippet.ToDocument(x)]);
        Assert.Equal(x, snippet.ToCode(snippet.ToDocument(x)));
    }

    [Fact]
    public void FunctionDeclarationsAfterStatementsStayOutside()
    {
        var snippet = GoSnippet.Wrap("handler := func(w io.Writer) {\n\tgreet(w)\n}\nhandler(os.Stdout)\n\n// greet greets.\nfunc greet(w io.Writer) {}\n\nfunc (p *point) Move() {}");

        Assert.Equal("package main\nfunc _() {\nhandler := func(w io.Writer) {\n\tgreet(w)\n}\nhandler(os.Stdout)\n\n}\n\n// greet greets.\nfunc greet(w io.Writer) {}\n\nfunc (p *point) Move() {}", snippet.Document);
    }

    [Fact]
    public void StringsCommentsAndContinuationsAreNotItems()
    {
        // A raw string with a line that looks like a declaration, a comment, and lines inside brackets.
        var snippet = GoSnippet.Wrap("query := `\nfunc main() {\n`\n/* func f() {} */\nrows, err := db.Query(\n\tquery,\n)");

        Assert.Equal("package main\nfunc _() {\nquery := `\nfunc main() {\n`\n/* func f() {} */\nrows, err := db.Query(\n\tquery,\n)\n}\n", snippet.Document);
    }

    [Fact]
    public void ParsesHovers()
    {
        Assert.Equal(("type Greeter struct{ Name string }", "Greeter greets people.\n\n\n```go\nfunc (g Greeter) Greet() string\n```"),
            GoChecker.ParseHover("```go\ntype Greeter struct{ Name string } // size=16 (0x10)\n```\n\n---\n\nGreeter greets people.\n\n\n```go\nfunc (g Greeter) Greet() string\n```"));

        Assert.Equal(("field Name string", null), GoChecker.ParseHover("```go\nfield Name string // size=16 (0x10), offset=0\n```"));

        // Links to the Go sources keep their text; a package's documentation is its first paragraph, without its details.
        Assert.Equal(("package fmt", "Package fmt implements formatted I/O: Print and Println."),
            GoChecker.ParseHover("```go\npackage fmt\n```\n\n---\n\nPackage fmt implements formatted I/O: [Print](file:///go/src/fmt/print.go#271,6) and [Println](file:///go/src/fmt/print.go#300,6).\n\n### Printing\n\nThere are four families."));
        Assert.Equal(("package main", null), GoChecker.ParseHover("```go\npackage main\n```\n\n---\n\n - Package path: snippets/s0\n - Module: snippets\n - Language version: go1.26"));

        Assert.Null(GoChecker.ParseHover("struct{Name string}\n"));
    }
}
