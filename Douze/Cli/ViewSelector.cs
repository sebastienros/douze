using Douze.Progress;

namespace Douze.Cli;

/// <summary>
/// Picks the progress view of a build from the command line and the console.
/// </summary>
internal static class ViewSelector
{
    public static IProgressView Select(CommandLine commandLine)
    {
        if (commandLine.Quiet)
        {
            return new SilentProgressView(Console.Error);
        }

        // TODO(phase 4): the live view, unless --plain, a redirected output, CI=true, TERM=dumb or NO_COLOR.
        return new PlainProgressView(Console.Out, commandLine.Verbose);
    }
}
