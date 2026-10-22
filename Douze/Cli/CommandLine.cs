using System.Globalization;
using Douze.Workflow;

namespace Douze.Cli;

internal enum Command { Build, Watch, Serve, Doctor, ToolsInstall, WorkerHelper, Help, Version }

/// <summary>
/// A command line usage error. Exit code 2.
/// </summary>
internal sealed class CommandLineException(string message) : Exception(message);

/// <summary>
/// The parsed command line.
/// </summary>
internal sealed class CommandLine
{
    public const string Usage = """
        Usage:
          douze [build] [options]          build once (default command)
          douze watch [options]            build, then rebuild on changes
          douze serve [options]            watch + dev server with live reload
          douze doctor [options]           check what the steps need (libvips, tools, commands) without building
          douze tools install [options]    download the tools the steps need into the cache folder
          douze worker-helper              print douze-worker.mjs to stdout

        Options:
          -s, --source <dir>             source folder (default: .)
          -o, --output <dir>             output folder (default: _site)
          -w, --workflow <file>          workflow file (default: <source>/douze.yml when it exists)
          -p, --parallelism <n>          max parallel units (default: processor count)
              --keep-going               report every error instead of stopping the failing step
              --fail-fast                stop everything on the first error
              --trace <file>             write a Chrome trace event file
              --timeline <file>          write a self-contained timeline.html
              --trace-units              include one span per file or group in the trace
              --plain                    one line per step, no live view
          -q, --quiet                    errors only
          -v, --verbose                  one line per file, stack traces
              --print-workflow           print the effective workflow as YAML and exit
              --no-cache                 ignore and do not write the disk cache
              --cache-dir <dir>          cache location (default: <source>/.douze)
              --port <n>                 dev server port (serve, default 8080)
          -h, --help                     show this help
              --version                  show the version
        """;

    public Command Command { get; private set; } = Command.Build;
    public string Source { get; private set; } = ".";
    public string? Output { get; private set; }        // null: WorkflowSource.DefaultOutput (the workflow file's site.output in phase 2)
    public string? Workflow { get; private set; }
    public int? Parallelism { get; private set; }
    public FailurePolicy Failure { get; private set; } = FailurePolicy.Default;
    public string? TracePath { get; private set; }
    public string? TimelinePath { get; private set; }
    public bool TraceUnits { get; private set; }
    public bool Plain { get; private set; }
    public bool Quiet { get; private set; }
    public bool Verbose { get; private set; }
    public bool PrintWorkflow { get; private set; }
    public bool NoCache { get; private set; }
    public string? CacheDirectory { get; private set; }
    public int Port { get; private set; } = 8080;

    /// <exception cref="CommandLineException">The arguments are invalid.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var result = new CommandLine();
        var i = 0;

        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            result.Command = args[0] switch
            {
                "build" => Command.Build,
                "watch" => Command.Watch,
                "serve" => Command.Serve,
                "doctor" => Command.Doctor,
                "tools" => Command.ToolsInstall,
                "worker-helper" => Command.WorkerHelper,
                var other => throw new CommandLineException($"unknown command '{other}'"),
            };

            i++;

            if (result.Command == Command.ToolsInstall)
            {
                if (args.Count < 2 || args[1] != "install")
                {
                    throw new CommandLineException(args.Count < 2 || args[1].StartsWith('-') ? "'tools' needs a subcommand: 'tools install'" : $"unknown tools subcommand '{args[1]}'");
                }

                i++;
            }
        }

        for (; i < args.Count; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-s" or "--source": result.Source = Value(); break;
                case "-o" or "--output": result.Output = Value(); break;
                case "-w" or "--workflow": result.Workflow = Value(); break;
                case "-p" or "--parallelism": result.Parallelism = Positive(); break;
                case "--keep-going": result.Failure = FailurePolicy.KeepGoing; break;
                case "--fail-fast": result.Failure = FailurePolicy.FailFast; break;
                case "--trace": result.TracePath = Value(); break;
                case "--timeline": result.TimelinePath = Value(); break;
                case "--trace-units": result.TraceUnits = true; break;
                case "--plain": result.Plain = true; break;
                case "-q" or "--quiet": result.Quiet = true; break;
                case "-v" or "--verbose": result.Verbose = true; break;
                case "--print-workflow": result.PrintWorkflow = true; break;
                case "--no-cache": result.NoCache = true; break;
                case "--cache-dir": result.CacheDirectory = Value(); break;
                case "--port": result.Port = Positive(); break;
                case "-h" or "--help": result.Command = Command.Help; break;
                case "--version": result.Command = Command.Version; break;
                default: throw new CommandLineException($"unknown option '{arg}'");
            }
        }

        if (args.Contains("--keep-going") && args.Contains("--fail-fast"))
        {
            throw new CommandLineException("'--keep-going' and '--fail-fast' cannot be used together");
        }

        if (result.Quiet && result.Verbose)
        {
            throw new CommandLineException("'--quiet' and '--verbose' cannot be used together");
        }

        return result;

        string Value()
        {
            if (i + 1 >= args.Count || args[i + 1].StartsWith('-') && args[i + 1].Length > 1)
            {
                throw new CommandLineException($"option '{args[i]}' needs a value");
            }

            return args[++i];
        }

        int Positive()
        {
            var name = args[i];
            var value = Value();

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                throw new CommandLineException($"option '{name}' needs a positive number, not '{value}'");
            }

            return number;
        }
    }

    /// <summary>
    /// Runs the command of <paramref name="args"/> and returns the exit code: 0 success, 1 build errors (or a missing
    /// requirement, a tool that cannot be installed), 2 usage or configuration error, 130 cancelled.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        CommandLine commandLine;

        try
        {
            commandLine = Parse(args);
        }
        catch (CommandLineException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            Console.Error.WriteLine("Run 'douze --help' for usage.");
            return ExitCodes.Usage;
        }

        switch (commandLine.Command)
        {
            case Command.Help:
                Console.WriteLine(Usage);
                return ExitCodes.Success;

            case Command.Version:
                Console.WriteLine(StepDefinition.DouzeVersion);
                return ExitCodes.Success;

            case Command.WorkerHelper:
                return WorkerHelperCommand.Run();

            case Command.Doctor:
                return await DoctorCommand.RunAsync(commandLine, Console.Out);

            case Command.ToolsInstall:
                return await ToolsCommand.InstallAsync(commandLine, Console.Out, Console.Error);

            case Command.Watch or Command.Serve:
                return await WatchCommand.RunAsync(commandLine);

            default:
                return await BuildCommand.RunAsync(commandLine);
        }
    }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int BuildErrors = 1;
    public const int Usage = 2;
    public const int Cancelled = 130;
}
