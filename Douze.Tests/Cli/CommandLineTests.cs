using Douze.Cli;
using Douze.Workflow.Yaml;
using Xunit;

namespace Douze.Tests.Cli;

public class CommandLineTests
{
    [Fact]
    public void VarsKeepTheirOrder()
    {
        var commandLine = CommandLine.Parse(["--var", "og=false", "--env", "token=GITHUB_TOKEN", "--var", "title=a=b", "--var", "empty="]);

        Assert.Equal(
            [VarOverride.FromValue("og", "false"), VarOverride.FromVariable("token", "GITHUB_TOKEN"), VarOverride.FromValue("title", "a=b"), VarOverride.FromValue("empty", "")],
            commandLine.Vars);
    }

    [Theory]
    [InlineData("--var", "og", "option '--var' needs <name>=<value>, not 'og'")]
    [InlineData("--var", "=1", "option '--var' needs <name>=<value>, not '=1'")]
    [InlineData("--env", "token=", "option '--env' needs <name>=<environment variable>, not 'token='")]
    public void InvalidVars(string option, string value, string message)
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse([option, value]));

        Assert.Equal(message, error.Message);
    }
}
