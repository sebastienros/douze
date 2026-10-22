using Douze.CodeHover.CSharp;
using Xunit;

namespace Douze.Tests.CodeHover;

public class CSharpSnippetTests
{
    private const string Main = "namespace __Snippet0 { static class __Program { static async global::System.Threading.Tasks.Task __Main(string[] args) {\n";

    [Fact]
    public void StatementsBecomeAMethodBody()
    {
        var snippet = CSharpSnippet.Wrap("var x = 1;\nConsole.WriteLine(x);", 0);

        Assert.Equal(Main + "var x = 1;\nConsole.WriteLine(x);\n} }\n\n}\n", snippet.Document);

        // Offsets map both ways; the wrapping maps to nothing, or to the code after it.
        var x = snippet.Code.IndexOf("x)", StringComparison.Ordinal);
        Assert.Equal('x', snippet.Document[snippet.ToDocument(x)]);
        Assert.Equal(x, snippet.ToCode(snippet.ToDocument(x)));
        Assert.Equal(-1, snippet.ToCode(5));
        Assert.Equal(0, snippet.ToCode(5, clamp: true));
    }

    [Fact]
    public void UsingDirectivesStayFirst()
    {
        var snippet = CSharpSnippet.Wrap("using System.Text;\nusing static System.Math;\nusing Alias = System.Collections.Generic.List<int>;\n\nvar text = new StringBuilder();", 3);

        Assert.StartsWith("using System.Text;\nusing static System.Math;\nusing Alias = System.Collections.Generic.List<int>;\nnamespace __Snippet3 { static class __Program", snippet.Document);
        Assert.EndsWith("{\n\nvar text = new StringBuilder();\n} }\n\n}\n", snippet.Document);
    }

    [Fact]
    public void UsingStatementsAreStatements()
    {
        Assert.StartsWith("namespace __Snippet0 { static class __Program", CSharpSnippet.Wrap("using var stream = File.OpenRead(\"a\");", 0).Document);
        Assert.StartsWith("namespace __Snippet0 { static class __Program", CSharpSnippet.Wrap("using (var stream = File.OpenRead(\"a\")) { }", 0).Document);
    }

    [Fact]
    public void DeclarationsAfterStatementsLeaveTheMethod()
    {
        var snippet = CSharpSnippet.Wrap("var p = new Point(1, 2);\nConsole.WriteLine(p);\n\nrecord Point(int X, int Y);", 0);

        Assert.Equal(Main + "var p = new Point(1, 2);\nConsole.WriteLine(p);\n\n} }\n\nrecord Point(int X, int Y);\n}\n", snippet.Document);
    }

    [Fact]
    public void BlocksAndLiteralsDoNotEndTheStatements()
    {
        // Braces, semicolons and keywords in strings, comments and blocks are not items.
        var code = """"
            if (args.Length > 0) { Console.WriteLine("}; class Fake {"); }
            else { var c = '}'; }
            var s = $"{(args.Length > 0 ? "{" : "x")}; class Fake";
            var verbatim = @"""}; class Fake {";
            var raw = """
                }; class Fake {
                """;
            // class Fake { }
            /* class Fake { } */
            var record = 1;
            record.ToString();
            """";

        var snippet = CSharpSnippet.Wrap(code, 0);

        Assert.Equal(Main + code + "\n} }\n\n}\n", snippet.Document);
    }

    [Fact]
    public void DeclarationsGetANamespace()
    {
        var snippet = CSharpSnippet.Wrap("// A type.\n[Serializable]\npublic sealed class Greeter\n{\n    public string Greet() => \"Hi\";\n}", 1);

        // The comments before a declaration (its documentation) stay with it.
        Assert.Equal("namespace __Snippet1 {\n// A type.\n[Serializable]\npublic sealed class Greeter\n{\n    public string Greet() => \"Hi\";\n}\n}\n", snippet.Document);
    }

    [Fact]
    public void FileScopedNamespacesBecomeBlocks()
    {
        var snippet = CSharpSnippet.Wrap("using System;\n\nnamespace Demo.App;\n\npublic class A { }", 0);

        Assert.Equal("using System;\nnamespace __Snippet0 {\n\nnamespace Demo.App{\n\npublic class A { }\n}\n}\n", snippet.Document);

        // The replaced ';' keeps its offset.
        var a = snippet.Code.IndexOf('A', StringComparison.Ordinal);
        Assert.Equal('A', snippet.Document[snippet.ToDocument(a)]);
    }

    [Fact]
    public void MembersGetAClass()
    {
        var snippet = CSharpSnippet.Wrap("public int Add(int a, int b) => a + b;\n\nprivate readonly int _count;", 0);

        Assert.Equal("namespace __Snippet0 { class __Members {\npublic int Add(int a, int b) => a + b;\n\nprivate readonly int _count;\n} }\n", snippet.Document);
    }

    [Fact]
    public void LocalFunctionsAreStatements()
    {
        Assert.StartsWith(Main, CSharpSnippet.Wrap("static int Twice(int x) => x * 2;\nConsole.WriteLine(Twice(2));", 0).Document);
        Assert.StartsWith(Main, CSharpSnippet.Wrap("async Task RunAsync() { await Task.Delay(1); }", 0).Document);
    }

    [Fact]
    public void Hovers()
    {
        var (signature, docs) = CSharpChecker.ParseHover("```csharp\nclass __Snippet2.Greeter\n```\n  \nGreets&nbsp;people\\.  \n  \nReturns:  \n&nbsp;&nbsp;A greeting\\.  \n")!.Value;

        Assert.Equal("class Greeter", signature);
        Assert.Equal("Greets people\\.\n\nReturns:\n  A greeting\\.", docs);

        Assert.Equal(("(local variable) int x", null), CSharpChecker.ParseHover("```csharp\n(local variable) int x\n```\n"));
        Assert.Null(CSharpChecker.ParseHover(""));
    }
}
