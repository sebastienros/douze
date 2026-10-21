namespace Douze.Cli;

/// <summary>
/// <c>douze worker-helper</c>: prints the Node helper of worker command steps.
/// </summary>
internal static class WorkerHelperCommand
{
    public static int Run()
    {
        // TODO(phase 3): print the embedded douze-worker.mjs.
        Console.Error.WriteLine("error: 'douze worker-helper' is not available yet");
        return ExitCodes.Usage;
    }
}
