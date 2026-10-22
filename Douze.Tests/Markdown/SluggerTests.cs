using Douze.Markdown;
using Xunit;

namespace Douze.Tests.Markdown;

public class SluggerTests
{
    // The ids GitHub gives these headings.
    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("C# and .NET", "c-and-net")]
    [InlineData("What's new?", "whats-new")]
    [InlineData("foo_bar-baz", "foo_bar-baz")]
    [InlineData("Two  spaces", "two--spaces")]
    [InlineData("Café crème", "café-crème")]
    [InlineData("日本語のタイトル", "日本語のタイトル")]
    [InlineData("Привет мир", "привет-мир")]
    [InlineData("🎉 Party time", "-party-time")]
    [InlineData("1. Getting started", "1-getting-started")]
    [InlineData("2024 roadmap", "2024-roadmap")]
    [InlineData("`code` & <tags>", "code--tags")]
    [InlineData("API (v2.0) / Reference", "api-v20--reference")]
    public void GitHubRules(string text, string slug)
    {
        Assert.Equal(slug, Slugger.Slug(text));
    }

    [Fact]
    public void DuplicatesAreSuffixed()
    {
        var scope = new Slugger.Scope();

        Assert.Equal("intro", scope.Unique("intro"));
        Assert.Equal("intro-1", scope.Unique("intro"));
        Assert.Equal("intro-2", scope.Unique("intro"));
        Assert.Equal("intro-1-1", scope.Unique("intro-1"));
    }

    [Fact]
    public void ReservedIdsAreNotGenerated()
    {
        var scope = new Slugger.Scope();
        scope.Reserve("setup");

        Assert.Equal("setup-1", scope.Unique("setup"));
    }

    [Fact]
    public void EmptySlugHasAFallback()
    {
        var scope = new Slugger.Scope();

        Assert.Equal("heading", scope.Unique(Slugger.Slug("🎉")));
        Assert.Equal("heading-1", scope.Unique(Slugger.Slug("🚀")));
    }
}
