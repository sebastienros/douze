namespace Douze.Cli;

/// <summary>
/// <c>douze worker-helper</c>: prints the Node helper of worker command steps, <c>douze-worker.mjs</c>.
/// </summary>
internal static class WorkerHelperCommand
{
    public static int Run()
    {
        using var stdout = Console.OpenStandardOutput();
        Write(stdout);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Writes the embedded <c>douze-worker.mjs</c>.
    /// </summary>
    public static void Write(Stream destination)
    {
        using var resource = typeof(WorkerHelperCommand).Assembly.GetManifestResourceStream("douze-worker.mjs")
            ?? throw new InvalidOperationException("The resource 'douze-worker.mjs' is missing.");

        resource.CopyTo(destination);
    }
}
