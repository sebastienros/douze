using Douze.Images;
using Douze.Workflow;
using SixLabors.Fonts;
using Xunit;

namespace Douze.Tests.Images;

public class FontResolverTests
{
    [Theory]
    [InlineData("Outfit Bold 56", "Outfit", FontStyle.Bold, "bold", 56f)]
    [InlineData("sans 32", "sans", FontStyle.Regular, null, 32f)]
    [InlineData("DejaVu Sans Bold Italic 24px", "DejaVu Sans", FontStyle.BoldItalic, "bold", 24f)]
    [InlineData("Inter Light 20.5", "Inter", FontStyle.Regular, "light", 20.5f)]
    [InlineData("serif italic", "serif", FontStyle.Italic, null, 16f)]
    [InlineData("Regular 12", "Regular", FontStyle.Regular, null, 12f)]
    public void ParsesPangoLikeDescriptions(string description, string family, FontStyle style, string? weight, float size)
    {
        var parsed = FontResolver.Parse(description);

        Assert.Equal(family, parsed.Families[0]);
        Assert.Equal((style, weight, size), (parsed.Style, parsed.Weight?.ToLowerInvariant(), parsed.Size));
    }

    [Fact]
    public void ParsesAFamilyList()
    {
        Assert.Equal(["Inter", "sans"], FontResolver.Parse("Inter, sans bold 20").Families);
        Assert.Equal(["sans"], FontResolver.Parse("24").Families);
    }

    [Fact]
    public void InvalidSize()
    {
        Assert.Equal("invalid font size '0' in font 'sans 0'", Assert.Throws<DouzeException>(() => FontResolver.Parse("sans 0")).Message);
    }

    [Fact]
    public void FontFileIsUsedByNameOrAsFallback()
    {
        var path = TestImages.FontFile();
        Assert.SkipWhen(path == null, "no font file on this machine");

        var bytes = File.ReadAllBytes(path!);
        var file = new FontSource(bytes, Hash128.Of(bytes));
        var resolver = new FontResolver();
        var family = resolver.Resolve("anything 10", file).Family.Name;

        Assert.Equal(family, resolver.Resolve($"{family} Bold 30", file).Family.Name);
        Assert.Equal(30, resolver.Resolve($"{family} Bold 30", file).Size);
        Assert.Equal(family, resolver.Resolve("No Such Family 12", file).Family.Name);
    }

    [Fact]
    public void UnknownFontWithoutAFile()
    {
        var error = Assert.Throws<DouzeException>(() => new FontResolver().Resolve("No Such Family 12", null));

        Assert.Equal("font 'No Such Family 12' not found; set 'fontfile' to a font file of the site, or install the font", error.Message);
    }

    [Fact]
    public void InvalidFontFile()
    {
        var bytes = "not a font"u8.ToArray();

        var error = Assert.Throws<DouzeException>(() => new FontResolver().Resolve("sans 12", new FontSource(bytes, Hash128.Of(bytes))));

        Assert.StartsWith("cannot read the font file", error.Message);
    }
}
