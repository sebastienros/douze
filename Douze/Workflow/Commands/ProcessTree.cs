using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Douze.Workflow.Commands;

/// <summary>
/// Kills a process with its descendants. On macOS and Linux every process gets a SIGKILL, without being stopped first:
/// <see cref="Process.Kill(bool)"/> stops the tree with SIGSTOP, and on macOS the runtime's SIGCHLD handler then loops
/// forever on the stopped child (<c>waitid</c> reports it, <c>waitpid</c> cannot reap it) while it holds the lock that
/// the kill, and every other <see cref="Process"/> call, waits for. Elsewhere, <see cref="Process.Kill(bool)"/>.
/// </summary>
internal static partial class ProcessTree
{
    private const int SIGKILL = 9;
    private const int ESRCH = 3;

    // Rounds of finding and killing descendants: a round kills what the previous ones missed, children forked meanwhile.
    private const int MaxRounds = 8;

    /// <exception cref="InvalidOperationException">The process was not started.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The process cannot be killed.</exception>
    public static void Kill(Process process)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            process.Kill(entireProcessTree: true);
            return;
        }

        // Its pid could already be another process's.
        if (process.HasExited)
        {
            return;
        }

        var root = process.Id;
        var killed = new HashSet<int>();

        // The root goes last, so the children it forks meanwhile still have it as their parent and the next round finds them.
        for (var round = 0; round < MaxRounds; round++)
        {
            var found = false;

            foreach (var pid in Descendants(root))
            {
                if (killed.Add(pid))
                {
                    found = true;

                    // A descendant that cannot be killed (another user's) must not spare the root.
                    SendSignal(pid, SIGKILL);
                }
            }

            if (!found)
            {
                break;
            }
        }

        if (SendSignal(root, SIGKILL) != 0 && Marshal.GetLastPInvokeError() is var error && error != ESRCH)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    /// <summary>
    /// The live descendants of <paramref name="root"/>, parents before their children.
    /// </summary>
    private static List<int> Descendants(int root)
    {
        var children = OperatingSystem.IsMacOS() ? (Func<int, IEnumerable<int>>)MacChildren : LinuxChildren();
        var descendants = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);

        while (queue.TryDequeue(out var parent))
        {
            foreach (var child in children(parent))
            {
                descendants.Add(child);
                queue.Enqueue(child);
            }
        }

        return descendants;
    }

    private static int[] MacChildren(int parent)
    {
        var buffer = new int[64];

        while (true)
        {
            var count = ListChildPids(parent, buffer, buffer.Length * sizeof(int));

            if (count <= 0)
            {
                return [];
            }

            // A full buffer may have dropped some.
            if (count < buffer.Length)
            {
                return buffer[..count];
            }

            buffer = new int[buffer.Length * 4];
        }
    }

    /// <summary>
    /// The children of every process, read from <c>/proc/&lt;pid&gt;/stat</c> once.
    /// </summary>
    private static Func<int, IEnumerable<int>> LinuxChildren()
    {
        var children = new Dictionary<int, List<int>>();

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            string stat;

            try
            {
                stat = File.ReadAllText(Path.Combine(directory, "stat"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // It exited.
                continue;
            }

            // "pid (comm) state ppid ...", where comm can hold spaces and parentheses.
            var fields = stat.AsSpan(stat.LastIndexOf(')') + 2);
            fields = fields[(fields.IndexOf(' ') + 1)..];

            if (int.TryParse(fields[..fields.IndexOf(' ')], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(pid);
            }
        }

        return parent => children.TryGetValue(parent, out var list) ? list : [];
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int pid, int signal);

    /// <returns>The number of pids written to <paramref name="buffer"/>, or -1.</returns>
    [LibraryImport("libproc", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static partial int ListChildPids(int parent, [Out] int[] buffer, int bufferSize);
}
