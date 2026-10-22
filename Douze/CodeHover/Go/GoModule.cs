using Douze.Workflow;

namespace Douze.CodeHover.Go;

/// <summary>
/// The module that Go snippets are checked in: the modules it requires (third-party imports of the snippets, by module
/// path and version), the shared files next to the snippets' packages (such as a package of stubs, which snippets
/// import as <c>snippets/&lt;folder&gt;</c>), and what selects it: fence titles (<c>main.go</c>) and patterns of the
/// code (<c>"net/http"</c>).
/// </summary>
internal sealed record GoModule(
    string Name,
    IReadOnlyDictionary<string, string> Requires,
    IReadOnlyList<SharedFile> Files,
    IReadOnlyList<string> Titles,
    IReadOnlyList<string> Patterns)
{
    /// <summary>
    /// The module path of every generated module.
    /// </summary>
    public const string Path = "snippets";

    /// <summary>
    /// The text of the <c>go.mod</c> file, for the language version <paramref name="goVersion"/> (<c>1.26.2</c>). The
    /// requirements are added by <c>go get</c>, which also adds those of the required modules.
    /// </summary>
    public static string ToModuleFile(string goVersion) => $"module {Path}\n\ngo {goVersion}\n";

    /// <summary>
    /// The arguments of <c>go get</c> that add the required modules: <c>path@version</c>.
    /// </summary>
    public IEnumerable<string> RequireArguments() => Requires.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}@{x.Value}");

    /// <summary>
    /// What the results of the module's snippets depend on.
    /// </summary>
    public IEnumerable<string> KeyParts()
    {
        yield return $"module:{Name}";

        foreach (var require in RequireArguments())
        {
            yield return $"require:{require}";
        }

        foreach (var file in Files)
        {
            yield return $"file:{file.Path}={Hash128.Of(file.Content)}";
        }
    }
}
