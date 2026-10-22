namespace Douze.Tools;

/// <summary>
/// A tool published on NuGet as one package per platform (a RID-specific <c>dotnet tool</c>, such as the Roslyn
/// language server): Douze fetches the package of the current platform over HTTP and extracts it, with no
/// <c>dotnet tool install</c>.
/// </summary>
/// <param name="Name">The tool's name, its folder under the tools folder.</param>
/// <param name="DefaultVersion">The version used when none is configured.</param>
/// <param name="Packages">The NuGet package id of each platform (<see cref="NpmTool.CurrentPlatform"/>).</param>
/// <param name="Executable">The path of the entry point in the package, where <c>{platform}</c> is the platform.</param>
/// <param name="KnownIntegrity">The integrity (<c>sha512-...</c>, the SHA-512 nuget.org gives) of the packages of
/// <paramref name="DefaultVersion"/> by platform: they are verified against these, with no other request. Other versions
/// are verified against the hash of their nuget.org catalog entry.</param>
internal sealed record NuGetTool(
    string Name,
    string DefaultVersion,
    IReadOnlyDictionary<string, string> Packages,
    string Executable,
    IReadOnlyDictionary<string, string> KnownIntegrity)
{
    /// <summary>
    /// The Roslyn language server (<c>roslyn-language-server</c>, the server of the C# extension of VS Code), published
    /// as <c>roslyn-language-server.&lt;rid&gt;</c>. It is framework dependent: it runs with <c>dotnet</c> (a .NET 10
    /// runtime or later), and loads projects with the .NET SDK.
    /// </summary>
    public static NuGetTool RoslynLanguageServer { get; } = new(
        "roslyn",
        "5.13.0-1.26514.10",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "roslyn-language-server.osx-arm64",
            ["osx-x64"] = "roslyn-language-server.osx-x64",
            ["linux-x64"] = "roslyn-language-server.linux-x64",
            ["linux-arm64"] = "roslyn-language-server.linux-arm64",
            ["linux-musl-x64"] = "roslyn-language-server.linux-musl-x64",
            ["linux-musl-arm64"] = "roslyn-language-server.linux-musl-arm64",
            ["win-x64"] = "roslyn-language-server.win-x64",
            ["win-arm64"] = "roslyn-language-server.win-arm64",
        },
        "tools/net10.0/{platform}/Microsoft.CodeAnalysis.LanguageServer.dll",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "sha512-2Jb1K/zED//VPVrZQMk9TdYxKAPVpYFdQWyR5quBkwlDfOTfmmvOJUpvyknSn5gEvOhDxUaC8Jvw2mBhl2SOsw==",
            ["osx-x64"] = "sha512-KtsZpvvq7t8KU0l+yFZaDhXx3MEnACjTan7I4e/QxC4HI165spiAVP3TO1dB/Eb7rfYTPQ89rbDnOiO08TAyyA==",
            ["linux-x64"] = "sha512-hiECk5jwtKqn8RvClv355qJL/wCiT5z1CjWza/99wgKJj0oZ/drlKD8LVYQ7zKM3gPVC4tFXbA+SfQ7PzQlzPw==",
            ["linux-arm64"] = "sha512-kj87Xl6/ctaL70mM+q7X3FHuJ4fjrK0lr1H9usa2EAxF/asM8Sya3GmXiIcExCE/iqhNkcOvYKJhXc6ghdvtNw==",
            ["linux-musl-x64"] = "sha512-stp4C5WuhwFvsRNoE6Yc+AYAGzHLVRWIUpUubidyr6ITPvoskN0NUr02bqBDSHo0CFF3ZC/21ukUrfQW57RzAw==",
            ["linux-musl-arm64"] = "sha512-zzjes47CTJ8VODy/sHxTQY6w32QAm4ZYhKWTnFaBnn8aLUNaJ4TgxZp8Xq1UuizrGFo9u3xBCJP13QsEGUEYDg==",
            ["win-x64"] = "sha512-+bE9gCtusnlR0SVlFdfc0remupBvkNf8y2NKNp7CrCDEfbZMgCfucEMyQ/c56BxxmVy8nKDxnYpL5KnziLWo1Q==",
            ["win-arm64"] = "sha512-zZd+KlY+3TS8xXd9VNWZ0DRmutD/LaFyEODgHriBo7zLWEniyQVCUqFqCmJPoADzKkgLDxE3rLyWRaG60Mqqnw==",
        });
}
