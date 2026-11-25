using System.Reflection;
using Douze.FileSystems;
using Douze.Nodes;
using Douze.Tests.Workflow.Cache;
using Douze.Tests.Workflow.Steps;
using Douze.Workflow;

namespace Douze.Tests.DotNetApi;

/// <summary>
/// Builds sites with the <c>dotnet-api</c> step over the assembly of <c>Douze.Tests.ApiSample</c> and its XML
/// documentation file, copied to <c>/lib/</c> of the source.
/// </summary>
public abstract class DotNetApiTestBase : CacheTestBase
{
    /// <summary>
    /// The output folder of <c>Douze.Tests.ApiSample</c>, built before the tests in the same configuration.
    /// </summary>
    protected static string SampleFolder { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Douze.Tests.ApiSample", "bin",
        typeof(DotNetApiTestBase).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
        Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))));

    protected static string SampleDll => Path.Combine(SampleFolder, "Contoso.Widgets.dll");

    protected static string SampleXml => Path.Combine(SampleFolder, "Contoso.Widgets.xml");

    internal async Task CopySampleAsync(string folder = "/lib/", bool xml = true)
    {
        await WriteBytesAsync(folder + "Contoso.Widgets.dll", await File.ReadAllBytesAsync(SampleDll, Xunit.TestContext.Current.CancellationToken));

        if (xml)
        {
            await WriteBytesAsync(folder + "Contoso.Widgets.xml", await File.ReadAllBytesAsync(SampleXml, Xunit.TestContext.Current.CancellationToken));
        }
    }

    internal async Task WriteBytesAsync(string path, byte[] bytes) => await Source.WriteBinaryFileAsync(new FPath(path), bytes);

    internal static StepDefinition Api(NodeObject? with = null, string[]? inputs = null) => StepTestBase.Step("api", "dotnet-api", inputs: inputs ?? ["lib/**"], with: with);

    /// <summary>
    /// The value the step contributed: <c>api</c>.
    /// </summary>
    internal static NodeObject ApiValue(BuildResult result, string name = "api", string key = "api")
    {
        return (NodeObject)result[name].Contributions.Site.Single().Values[key]!;
    }

    internal static NodeObject Type(NodeObject api, string fullName)
    {
        return (NodeObject)((NodeArray)api["types"]!).Single(x => x!["fullName"]!.GetValue<string>() == fullName)!;
    }

    internal static NodeObject Member(NodeObject type, string list, string displayName)
    {
        return (NodeObject)((NodeArray)type[list]!).Single(x => x!["displayName"]!.GetValue<string>() == displayName)!;
    }

    internal static string? Text(Node? node, string key) => node?[key]?.GetValue<string>();
}
