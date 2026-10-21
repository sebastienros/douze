using Douze.Cli;
using Douze.Progress;
using Xunit;

namespace Douze.Tests.Cli;

public class ViewSelectorTests
{
    private static IProgressView Select(string[] args, bool redirected = false, params (string Name, string Value)[] environment)
    {
        var variables = environment.ToDictionary(x => x.Name, x => x.Value);
        return ViewSelector.Select(CommandLine.Parse(args), redirected, name => variables.GetValueOrDefault(name));
    }

    [Fact]
    public void QuietPrintsErrorsOnly()
    {
        Assert.IsType<SilentProgressView>(Select(["--quiet"]));
        Assert.IsType<SilentProgressView>(Select(["-q"], redirected: true, ("CI", "true")));
    }

    [Fact]
    public void InteractiveTerminalGetsTheLiveView()
    {
        Assert.IsType<SpectreProgressView>(Select([]));
        Assert.IsType<SpectreProgressView>(Select(["-v"], environment: ("TERM", "xterm-256color")));

        // NO_COLOR removes the colors, not the live view: Spectre honors it.
        Assert.IsType<SpectreProgressView>(Select([], environment: ("NO_COLOR", "1")));
    }

    [Fact]
    public void PlainOrRedirectedOutputGetsThePlainView()
    {
        Assert.IsType<PlainProgressView>(Select(["--plain"]));
        Assert.IsType<PlainProgressView>(Select([], redirected: true));
        Assert.IsType<PlainProgressView>(Select([], environment: ("TERM", "dumb")));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void ContinuousIntegration(string value, bool plain)
    {
        var view = Select([], environment: ("CI", value));
        Assert.Equal(plain ? typeof(PlainProgressView) : typeof(SpectreProgressView), view.GetType());
    }
}
