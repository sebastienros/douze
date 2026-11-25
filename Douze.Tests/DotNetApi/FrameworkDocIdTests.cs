using System.Runtime.InteropServices;
using System.Xml.Linq;
using Douze.DotNetApi;
using Xunit;

namespace Douze.Tests.DotNetApi;

/// <summary>
/// Reads reference assemblies of the SDK running the tests, whose XML documentation files come with them: every
/// documented member must have the documentation comment ID <see cref="AssemblyReader"/> gives it, as
/// <see cref="DocSet"/> compares them (the documentation of .NET writes the type arguments of an explicit
/// implementation's name with <c>@</c> and sometimes in <c>&lt;&gt;</c>, the compiler with <c>,</c> in <c>{}</c>).
/// These assemblies pass with the reference packs of .NET 9, 10 and 11. Those of System.Runtime and System.Memory are
/// not tested: their documentation has IDs that the compiler never writes (<c>T[]</c> for <c>`0[]</c>, no <c>@</c> for
/// a by-reference <c>this</c> parameter), and members that the assembly no longer has.
/// </summary>
public sealed class FrameworkDocIdTests
{
    [Theory]
    [InlineData("System.Collections")]
    [InlineData("System.Collections.Immutable")]
    [InlineData("System.Linq")]
    [InlineData("System.Net.Http")]
    [InlineData("System.Text.Json")]
    [InlineData("System.Threading.Channels")]
    public void EveryDocumentedMemberIsFound(string name)
    {
        var dll = ReferenceAssembly(name);

        if (dll == null || !File.Exists(Path.ChangeExtension(dll, ".xml")))
        {
            Assert.Skip($"no reference assembly and documentation of {name} in the SDK");
        }

        var assembly = AssemblyReader.Read(File.ReadAllBytes(dll), dll, includeProtected: true);
        var ids = assembly.Types.Select(x => x.Id).Concat(assembly.Types.SelectMany(x => x.Members).Select(x => x.Id)).Select(DocSet.Normalize).ToHashSet(StringComparer.Ordinal);
        var documented = XDocument.Load(Path.ChangeExtension(dll, ".xml")).Root!.Element("members")!.Elements("member").Select(x => DocSet.Normalize((string)x.Attribute("name")!)).ToList();

        Assert.NotEmpty(documented);

        // Every ID that differs, so that a failure on another SDK says which ones.
        var missing = documented.Where(x => !ids.Contains(x)).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} of {documented.Count} documented members are not found:\n{string.Join("\n", missing.Take(50))}");
    }

    /// <summary>
    /// The reference assembly of the targeting pack of the running runtime: <c>packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/net&lt;major&gt;.0/</c>.
    /// </summary>
    private static string? ReferenceAssembly(string name)
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");

        if (!Directory.Exists(packs))
        {
            return null;
        }

        var framework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";

        return Directory.GetDirectories(packs)
            .Select(x => Path.Combine(x, "ref", framework, name + ".dll"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .LastOrDefault();
    }
}
