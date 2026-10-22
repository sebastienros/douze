using System.Security;
using System.Text;
using Douze.Workflow;

namespace Douze.CodeHover.CSharp;

/// <summary>
/// The project that C# snippets are checked in, as a <c>csproj</c>: its SDK, target framework, package references,
/// global usings and MSBuild properties, the shared files next to the snippets, the class that statements run in (whose
/// members are the variables that fragments use: <see cref="CSharpSnippet"/>), and what selects it: fence titles
/// (<c>Program.cs</c>) and patterns of the code (<c>WebApplication\.</c>).
/// </summary>
internal sealed record CSharpProject(
    string Name,
    string Sdk,
    string Framework,
    IReadOnlyDictionary<string, string> Packages,
    IReadOnlyList<string> Usings,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<SharedFile> Files,
    string? Context,
    IReadOnlyList<string> Titles,
    IReadOnlyList<string> Patterns)
{
    /// <summary>
    /// The SDK and target framework of a project that does not set them.
    /// </summary>
    public const string DefaultSdk = "Microsoft.NET.Sdk";

    public const string DefaultFramework = "net10.0";

    // What every project sets, under its own properties: snippets are a library (the wrapping has no entry point),
    // without the warnings that the wrapping or documentation code causes (an async method without await, an unused
    // local function or variable, a public member without documentation) and without the analyzers, which only cost
    // time. Documentation comments are only read (for the hovers of the snippets' own symbols) with a documentation
    // file.
    private static readonly KeyValuePair<string, string>[] DefaultProperties =
    [
        new("OutputType", "Library"),
        new("ImplicitUsings", "enable"),
        new("Nullable", "enable"),
        new("NoWarn", "$(NoWarn);CS1998;CS8321;CS0168;CS0219;CS1591"),
        new("TreatWarningsAsErrors", "false"),
        new("RunAnalyzers", "false"),
        new("EnableNETAnalyzers", "false"),
        new("GenerateDocumentationFile", "true"),
    ];

    /// <summary>
    /// The text of the project file.
    /// </summary>
    public string ToProjectFile()
    {
        var xml = new StringBuilder();
        xml.Append("<Project Sdk=\"").Append(Escape(Sdk)).Append("\">\n  <PropertyGroup>\n");
        xml.Append("    <TargetFramework>").Append(Escape(Framework)).Append("</TargetFramework>\n");

        foreach (var (name, value) in DefaultProperties.Where(x => !Properties.ContainsKey(x.Key)).Concat(Properties.OrderBy(x => x.Key, StringComparer.Ordinal)))
        {
            xml.Append("    <").Append(name).Append('>').Append(Escape(value)).Append("</").Append(name).Append(">\n");
        }

        xml.Append("  </PropertyGroup>\n  <ItemGroup>\n");

        foreach (var name in Usings)
        {
            xml.Append("    <Using Include=\"").Append(Escape(name)).Append("\" />\n");
        }

        foreach (var (id, version) in Packages.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            xml.Append("    <PackageReference Include=\"").Append(Escape(id)).Append("\" Version=\"").Append(Escape(version)).Append("\" />\n");
        }

        xml.Append("  </ItemGroup>\n</Project>\n");
        return xml.ToString();
    }

    /// <summary>
    /// What the results of the project's snippets depend on.
    /// </summary>
    public IEnumerable<string> KeyParts()
    {
        yield return $"project:{Name}";
        yield return ToProjectFile();
        yield return $"context:{Context}";

        foreach (var file in Files)
        {
            yield return $"file:{file.Path}={Hash128.Of(file.Content)}";
        }
    }

    private static string Escape(string value) => SecurityElement.Escape(value);
}
