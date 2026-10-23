using Douze.Liquid;
using Douze.Validation;
using Xunit;

namespace Douze.Tests.Validation;

public class CodeValidatorsTests
{
    [Fact]
    public void YamlAcceptsEveryDocumentOfAStream()
    {
        Assert.Empty(CodeValidators.CheckYaml("a: 1\n---\nb: [1, 2]\n"));
        Assert.Empty(CodeValidators.CheckYaml(""));
    }

    [Fact]
    public void YamlReportsTheSyntaxErrorWithItsPosition()
    {
        var error = Assert.Single(CodeValidators.CheckYaml("steps:\n  a:\n    uses: x\n   needs: y\n"));

        Assert.Equal(4, error.Line);
        Assert.StartsWith("line 4, column ", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Lin:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void YamlRejectsTabsInIndentation()
    {
        Assert.NotEmpty(CodeValidators.CheckYaml("a:\n\tb: 1\n"));
    }

    [Fact]
    public void YamlReportsDuplicateKeys()
    {
        var errors = CodeValidators.CheckYaml("a: 1\nb:\n  c: 1\n  c: 2\na: 3\n<<: {}\n");

        Assert.Equal(["line 4, column 3: duplicate key 'c'", "line 5, column 1: duplicate key 'a'"], errors.Select(x => x.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void JsonReportsTheSyntaxErrorWithItsPosition()
    {
        var error = Assert.Single(CodeValidators.CheckJson("{\n  \"a\": 1,\n  \"b\" 2\n}", comments: false));

        Assert.Equal((3, 7), (error.Line, error.Column));
        Assert.DoesNotContain("LineNumber", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsoncAllowsCommentsAndTrailingCommas()
    {
        const string code = "{\n  // a comment\n  \"a\": [1, 2,],\n}";

        Assert.Empty(CodeValidators.CheckJson(code, comments: true));
        Assert.NotEmpty(CodeValidators.CheckJson(code, comments: false));
    }

    [Fact]
    public void JsonLinesHaveAValuePerLine()
    {
        Assert.Empty(CodeValidators.CheckJsonLines("{\"type\":\"hello\"}\n\n{\"type\":\"file\"}"));

        var error = Assert.Single(CodeValidators.CheckJsonLines("{\"a\":1}\n{\"b\":}"));
        Assert.Equal((2, 6), (error.Line, error.Column));
    }

    [Fact]
    public void JsonRejectsDuplicateProperties()
    {
        Assert.NotEmpty(CodeValidators.CheckJson("{ \"a\": 1, \"a\": 2 }", comments: false));
    }

    [Fact]
    public void XmlChecksDocumentsAndFragments()
    {
        Assert.Empty(CodeValidators.CheckXml("<?xml version=\"1.0\"?>\n<a><b/></a>", fragment: false));
        Assert.Empty(CodeValidators.CheckXml("<a/>\n<b/>", fragment: true));
        Assert.NotEmpty(CodeValidators.CheckXml("<a/>\n<b/>", fragment: false));

        var error = Assert.Single(CodeValidators.CheckXml("<a>\n  <b>\n</a>", fragment: false));
        Assert.Equal(3, error.Line);
        Assert.DoesNotContain("Line 3, position", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlIgnoresTheDtdAndFetchesNothing()
    {
        Assert.Empty(CodeValidators.CheckXml("<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>", fragment: false));
    }

    [Fact]
    public void LiquidUsesTheParserItIsGiven()
    {
        var parser = LiquidOptions.CreateParser();

        Assert.Empty(CodeValidators.CheckLiquid("{% for x in items %}{{ x | upcase }}{% endfor %}", parser));

        var error = Assert.Single(CodeValidators.CheckLiquid("a\n{% if x %}\nb", parser));
        Assert.True(error.Line > 0);
    }

    [Fact]
    public async Task WorkflowReportsUnknownKeys()
    {
        var error = Assert.Single(await CheckWorkflowAsync("site:\n  urll: https://example.com\n"));

        Assert.Equal((2, 3), (error.Line, error.Column));
        Assert.StartsWith("unknown key 'urll' in 'site'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowReportsUnknownStepsAndOptions()
    {
        const string code = """
            steps:
              pages:
                uses: frontmatter
                with:
                  requires: [title]
              other:
                needs: missing
                uses: nothing
            """;

        var errors = await CheckWorkflowAsync(code);

        Assert.Collection(errors.OrderBy(x => x.Line),
            x => Assert.Contains("requires", x.Message, StringComparison.Ordinal),
            x => Assert.Contains("unknown built-in step 'nothing'", x.Message, StringComparison.Ordinal));

        // 'needs: missing' is not an error: a sample shows part of a workflow.
        Assert.DoesNotContain(errors, x => x.Message.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowChecksAnExcerptOfSteps()
    {
        Assert.Empty(await CheckWorkflowAsync("js:\n  needs: permalink\n  run: \"npx esbuild\"\nrender:\n  needs: js\n  uses: liquid\n"));

        // Positions are the excerpt's.
        var error = Assert.Single(await CheckWorkflowAsync("render:\n  needs: js\n  uses: liquid\n  with:\n    strictt: true\n"));
        Assert.Equal(1, error.Line);
        Assert.Contains("strictt", error.Message, StringComparison.Ordinal);

        var field = Assert.Single(await CheckWorkflowAsync("render:\n  needs: js\n  uses: liquid\n  matchh: x\n"));
        Assert.Equal((4, 3), (field.Line, field.Column));
    }

    [Fact]
    public async Task WorkflowOfOtherSectionsOnly()
    {
        Assert.Empty(await CheckWorkflowAsync("markdown:\n  extensions: [pipetables]\n"));
        Assert.Single(await CheckWorkflowAsync("markdown:\n  extensions: [tabels]\n"));
    }

    private static async Task<IReadOnlyList<CodeError>> CheckWorkflowAsync(string code)
    {
        Assert.Empty(CodeValidators.CheckYaml(code, out var stream));
        return await WorkflowSnippet.CheckAsync(code, stream!);
    }
}
