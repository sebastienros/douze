using System.Collections.Immutable;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Workflow;
using Douze.Workflow.Cache;
using Xunit;

namespace Douze.Tests.Workflow.Cache;

public class ResultKeyTests
{
    private static readonly StepDefinition Markdown = new() { Name = "markdown", Uses = "markdown" };

    private static FileRef Ref(string path, string content = "content", string metadata = "metadata") => new(new FPath(path), Hash128.OfText(content), Hash128.OfText(metadata));

    private static Hash128 Key(StepDefinition definition, params FileRef[] inputs) => ResultKey.Compute(definition, [.. inputs], Hash128.Empty);

    [Fact]
    public void Stable()
    {
        // The same step and inputs give the same key, whatever the order of the inputs.
        Assert.Equal(Key(Markdown, Ref("/a.md"), Ref("/b.md")), Key(Markdown, Ref("/b.md"), Ref("/a.md")));
        Assert.Equal(Key(Markdown, Ref("/a.md")), Key(Markdown with { }, Ref("/a.md")));

        // Where the step is declared and what it needs are not part of its results.
        Assert.Equal(Key(Markdown, Ref("/a.md")), Key(Markdown with { Needs = ["pages"], Location = new SourceLocation("douze.yml", 3, 5) }, Ref("/a.md")));
    }

    [Fact]
    public void ChangesWithConfig()
    {
        var key = Key(Markdown, Ref("/a.md"));

        Assert.NotEqual(key, Key(Markdown with { Name = "other" }, Ref("/a.md")));
        Assert.NotEqual(key, Key(Markdown with { With = new NodeObject { ["extensions"] = NodeValue.Create("advanced") } }, Ref("/a.md")));
        Assert.NotEqual(key, Key(Markdown with { Match = ["**/*.markdown"] }, Ref("/a.md")));
    }

    [Fact]
    public void ChangesWithAnInput()
    {
        var key = Key(Markdown, Ref("/a.md"));

        Assert.NotEqual(key, Key(Markdown, Ref("/b.md")));
        Assert.NotEqual(key, Key(Markdown, Ref("/a.md", content: "other")));
        Assert.NotEqual(key, Key(Markdown, Ref("/a.md", metadata: "other")));
        Assert.NotEqual(key, Key(Markdown, Ref("/a.md"), Ref("/b.md")));

        // A step that reads only metadata does not depend on contents.
        ImmutableArray<FileRef> original = [Ref("/a.md")];
        ImmutableArray<FileRef> edited = [Ref("/a.md", content: "other")];
        Assert.Equal(ResultKey.Compute(Markdown, original, Hash128.Empty, contents: false), ResultKey.Compute(Markdown, edited, Hash128.Empty, contents: false));
        Assert.NotEqual(ResultKey.Compute(Markdown, original, Hash128.Empty, contents: false), ResultKey.Compute(Markdown, [Ref("/a.md", metadata: "other")], Hash128.Empty, contents: false));
    }

    [Fact]
    public void ChangesWithCommandVersion()
    {
        ImmutableArray<FileRef> inputs = [Ref("/a.md")];

        Assert.NotEqual(ResultKey.Compute(Markdown, inputs, Hash128.OfText("1")), ResultKey.Compute(Markdown, inputs, Hash128.OfText("2")));
        Assert.Equal(ResultKey.Compute(Markdown, inputs, Hash128.OfText("1")), ResultKey.Compute(Markdown, inputs, Hash128.OfText("1")));
    }
}
