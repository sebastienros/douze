using Douze.Progress;
using Spectre.Console;

namespace Douze.Cli;

/// <summary>
/// Picks the progress view of a build from the command line and the console.
/// </summary>
internal static class ViewSelector
{
    public static IProgressView Select(CommandLine commandLine)
    {
        return Select(commandLine, Console.IsOutputRedirected, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// <c>--quiet</c> prints errors only. <c>--plain</c>, a redirected output, <c>CI</c> or <c>TERM=dumb</c> print one
    /// line per step. Otherwise the live view runs; it honors <c>NO_COLOR</c> on its own.
    /// </summary>
    internal static IProgressView Select(CommandLine commandLine, bool outputRedirected, Func<string, string?> environment)
    {
        if (commandLine.Quiet)
        {
            return new SilentProgressView(Console.Error);
        }

        if (commandLine.Plain || outputRedirected || IsCI(environment("CI")) || environment("TERM") == "dumb")
        {
            return new PlainProgressView(Console.Out, commandLine.Verbose);
        }

        return new SpectreProgressView(AnsiConsole.Console, commandLine.Verbose);
    }

    private static bool IsCI(string? value)
    {
        return !string.IsNullOrEmpty(value) && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
