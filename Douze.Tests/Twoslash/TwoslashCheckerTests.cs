using Douze.Twoslash;
using Xunit;

namespace Douze.Tests.Twoslash;

public class TwoslashCheckerTests
{
    [Theory]
    [InlineData("f(1)", 2)]
    [InlineData("f (\n1)", 3)]
    [InlineData("f<string>(1)", 10)]
    [InlineData("f<Map<string, number>>()", 23)]
    [InlineData("f<(a: string) => void>()", 23)]
    [InlineData("f?.(1)", 4)]
    [InlineData("f.g(1)", null)]
    [InlineData("f < 1; g(2)", null)]
    [InlineData("f = 1", null)]
    [InlineData("f", null)]
    public void ArgumentsStartAfterTheParenthesisOfACall(string code, int? expected)
    {
        Assert.Equal(expected, TwoslashChecker.ArgumentsStart(code, 1));
    }
}
