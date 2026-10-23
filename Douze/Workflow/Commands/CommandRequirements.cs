using System.Text.RegularExpressions;

namespace Douze.Workflow.Commands;

/// <summary>
/// The requirements of a command step: its working directory, when <c>cwd</c> is set, and the executable its
/// <c>run</c> line starts with, found on <c>PATH</c> or, when it is a path, relative to the working directory. Nothing
/// is run: the version of the executable is not known.
/// </summary>
internal static partial class CommandRequirements
{
    // Commands of the shells that are not executables: the run line of a shell step can start with them.
    private static readonly HashSet<string> PosixBuiltIns = new(StringComparer.Ordinal)
    {
        ".", ":", "[", "alias", "cd", "command", "echo", "eval", "exec", "exit", "export", "false", "printf", "read",
        "set", "shift", "source", "test", "times", "trap", "true", "type", "ulimit", "umask", "unset", "wait",
    };

    private static readonly HashSet<string> CmdBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo", "endlocal", "erase",
        "exit", "for", "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path", "popd", "prompt", "pushd", "rd",
        "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time", "title", "type", "ver", "verify", "vol",
    };

    public static IEnumerable<Requirement> Create(StepDefinition definition, BuildContext context)
    {
        // A site that is not on disk (tests, memory file systems) runs its commands in the current folder.
        var root = context.Site.SourceDirectory ?? Environment.CurrentDirectory;
        var directory = Path.GetFullPath(Path.Combine(root, definition.WorkingDirectory ?? "."));
        var step = definition.Name;

        if (definition.WorkingDirectory != null)
        {
            yield return Requirement.Create("working directory", $"create the folder, or fix the option 'cwd' of step '{step}' (relative to the source folder)", _ =>
                ValueTask.FromResult(Directory.Exists(directory) ? RequirementStatus.Found(path: directory) : RequirementStatus.Missing($"'{directory}' does not exist")));
        }

        var tool = ToolRequirement(definition, context);

        if (tool != null)
        {
            yield return tool;
        }

        var executable = FirstWord(definition.Run ?? "", definition.Shell);

        // A run line that starts with the tool's binary ({{ tool }}).
        if (tool != null && executable == null)
        {
            yield break;
        }

        if (executable == null)
        {
            yield return Requirement.Create("command", "", _ => ValueTask.FromResult(RequirementStatus.Unchecked("is not known before the build: the 'run' line does not start with a plain executable")));
            yield break;
        }

        if (IsPath(executable))
        {
            yield return Requirement.Create(executable, $"check the 'run' line and the 'cwd' of step '{step}': a relative path is resolved against the working directory", _ =>
            {
                var path = Path.GetFullPath(Path.Combine(directory, executable));
                return ValueTask.FromResult(Candidates(path).FirstOrDefault(File.Exists) is { } found
                    ? IsExecutable(found) ? RequirementStatus.Found(path: found) : RequirementStatus.Missing($"is not executable ('{found}'): make it executable (chmod +x), or run it with its interpreter")
                    : RequirementStatus.Missing($"does not exist at '{path}'"));
            });
            yield break;
        }

        if (definition.Shell && (OperatingSystem.IsWindows() ? CmdBuiltIns : PosixBuiltIns).Contains(executable))
        {
            yield return Requirement.Create(executable, "", _ => ValueTask.FromResult(new RequirementStatus(RequirementState.Found, Detail: "is a command of the shell")));
            yield break;
        }

        yield return Requirement.Create(executable, $"install {executable} and add its folder to PATH, or change the 'run' line of step '{step}'", _ =>
            ValueTask.FromResult(FindOnPath(executable, directory) is { } found ? RequirementStatus.Found(path: found) : RequirementStatus.Missing("is not on PATH")));
    }

    /// <summary>
    /// The tool of a step's <c>tool</c> option, which Douze downloads (<see cref="Tools.NpmTool.Known"/>), or null.
    /// </summary>
    /// <exception cref="WorkflowValidationException">Douze does not know the tool.</exception>
    public static Tools.ToolRequirement? ToolRequirement(StepDefinition definition, BuildContext context)
    {
        if (definition.Tool is not { } option)
        {
            return null;
        }

        var (tool, version) = Tools.NpmTool.Parse(option)
            ?? throw new WorkflowValidationException($"unknown tool '{option}' on step '{definition.Name}'; Douze knows {Yaml.WorkflowSchema.Expected([.. Tools.NpmTool.Known.Keys.Order(StringComparer.Ordinal)])}", definition.Location);
        var installer = new Tools.ToolInstaller(context.ToolsRoot, null, (level, message) => context.View.Message(level, definition.Name, message));
        return new Tools.NpmToolRequirement(tool, version, installer, $"run 'douze tools install', or remove the option 'tool' of step '{definition.Name}' and run a {tool.Name} on PATH");
    }

    /// <summary>
    /// The executable a command line starts with, or null when it is not a plain word: a template, a subshell, a
    /// redirection. In a shell line, variable assignments before it are skipped, and the operators after it removed.
    /// </summary>
    internal static string? FirstWord(string run, bool shell)
    {
        IReadOnlyList<string> words;

        try
        {
            words = ProcessRunner.Split(run);
        }
        catch (DouzeException)
        {
            return null;
        }

        foreach (var word in words)
        {
            if (shell && Assignment().IsMatch(word))
            {
                continue;
            }

            var executable = shell ? word[..(word.IndexOfAny([';', '|', '&', '<', '>', '(', ')']) is var end and >= 0 ? end : word.Length)] : word;

            return executable.Length == 0 || executable.Contains("{{", StringComparison.Ordinal) || executable.Contains("{%", StringComparison.Ordinal)
                || executable[0] is '$' or '(' or '{' or '!' or '`' ? null : executable;
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    private static bool IsPath(string executable)
    {
        return executable.Contains('/', StringComparison.Ordinal) || OperatingSystem.IsWindows() && (executable.Contains('\\', StringComparison.Ordinal) || executable.Contains(':', StringComparison.Ordinal));
    }

    /// <summary>
    /// The full path of <paramref name="executable"/> in the folders of <c>PATH</c> (on Windows, the working directory
    /// first, as <c>cmd</c> does), or null.
    /// </summary>
    internal static string? FindOnPath(string executable, string workingDirectory)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (OperatingSystem.IsWindows())
        {
            folders = [workingDirectory, .. folders];
        }

        foreach (var folder in folders)
        {
            string path;

            try
            {
                path = Path.Combine(folder.Trim('"'), executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (Candidates(path).FirstOrDefault(x => File.Exists(x) && IsExecutable(x)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The files <paramref name="path"/> can name: on Windows, with each extension of <c>PATHEXT</c> when it has none.
    /// </summary>
    private static IEnumerable<string> Candidates(string path)
    {
        yield return path;

        if (OperatingSystem.IsWindows() && !Path.HasExtension(path))
        {
            foreach (var extension in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return path + extension.ToLowerInvariant();
            }
        }
    }

    private static bool IsExecutable(string path)
    {
        return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
