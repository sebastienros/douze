using Douze.Highlighting;
using Xunit;

namespace Douze.Tests.Highlighting;

/// <summary>
/// The color arithmetic of contrast adjustments, against the values of Expressive Code 0.44 (tinycolor) for the same
/// inputs.
/// </summary>
public class ColorTests
{
    [Theory]
    [InlineData("#91889b", "#27212e", "#9e97a7")]
    [InlineData("#7b6995", "#27212e", "#a194b4")]
    [InlineData("#ffe261", "#ffffff", "#76682d")]
    [InlineData("#b381c5", "#ffffff", "#7e5b8b")]
    [InlineData("#000000", "#000000", "#838383")]
    [InlineData("#ff000080", "#ffffff", "#d30000fc")]
    [InlineData("#ffffff", "#27212e", "#ffffff")]
    [InlineData("#357b42", "#9ab6ff99", "#1d4324")]
    public void EnsureContrast(string color, string background, string expected)
    {
        Assert.Equal(expected, Color.EnsureContrast(Color.Parse(color), Color.Parse(background), 5.5));
    }

    [Theory]
    [InlineData("lch(32% 45 284 / 60%)", "#174a9099")]
    [InlineData("lch(75% 45 136 / 60%)", "#8ec77d99")]
    [InlineData("#FFF", "#ffffff")]
    [InlineData("#0f08", "#00ff0088")]
    [InlineData("rgb(255, 0, 0)", "#ff0000")]
    [InlineData("transparent", "#00000000")]
    public void Parse(string text, string hex)
    {
        Assert.Equal(hex, Color.Parse(text).ToHex());
    }

    [Fact]
    public void OnBackground()
    {
        Assert.Equal("#c2d3ff", Color.Parse("#9ab6ff99").OnBackground(Color.White).ToHex());
    }

    [Fact]
    public void Invalid()
    {
        Assert.False(Color.TryParse("not a color", out _));
        Assert.False(Color.TryParse("#12345", out _));
    }
}
