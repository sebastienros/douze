using System.Runtime.InteropServices;

namespace Douze.Tools;

/// <summary>
/// A native tool published on npm as one package per platform, each a plain tarball with the binary (how TypeScript 7,
/// tsrs, Pagefind, esbuild and others ship): Douze fetches the package of the current platform over HTTP, with no Node.
/// </summary>
/// <param name="Name">The tool's name, its folder under the tools folder.</param>
/// <param name="DefaultVersion">The version used when none is configured.</param>
/// <param name="Packages">The npm package of each platform (<see cref="CurrentPlatform"/>).</param>
/// <param name="Executable">The path of the binary in the tarball, without <c>.exe</c> (added on Windows).</param>
/// <param name="KnownIntegrity">The integrity (<c>sha512-...</c>) of the packages of <paramref name="DefaultVersion"/>
/// by platform: they are verified against these, with no registry request. Other versions are verified against the
/// integrity the registry gives.</param>
internal sealed record NpmTool(
    string Name,
    string DefaultVersion,
    IReadOnlyDictionary<string, string> Packages,
    string Executable,
    IReadOnlyDictionary<string, string> KnownIntegrity)
{
    /// <summary>
    /// TypeScript 7, Microsoft's native compiler and language server (<c>tsc --lsp --stdio</c>), published as
    /// <c>@typescript/typescript-&lt;os&gt;-&lt;arch&gt;</c> for every platform Douze ships on. Its Linux binaries are
    /// static: the musl platforms use them too.
    /// </summary>
    public static NpmTool TypeScript { get; } = new(
        "typescript",
        "7.0.2",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "@typescript/typescript-darwin-arm64",
            ["osx-x64"] = "@typescript/typescript-darwin-x64",
            ["linux-x64"] = "@typescript/typescript-linux-x64",
            ["linux-musl-x64"] = "@typescript/typescript-linux-x64",
            ["linux-arm64"] = "@typescript/typescript-linux-arm64",
            ["linux-musl-arm64"] = "@typescript/typescript-linux-arm64",
            ["linux-arm"] = "@typescript/typescript-linux-arm",
            ["win-x64"] = "@typescript/typescript-win32-x64",
            ["win-arm64"] = "@typescript/typescript-win32-arm64",
        },
        "package/lib/tsc",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "sha512-gowzar9MwS/aRWp6f3a4KUqzRjAZjOsmGNCM6LcTgXum+dBfgsBVMN+AgvOCCbguXyick6LJhpBszxMebJ8syA==",
            ["osx-x64"] = "sha512-SZ9xZInqApNlNGc9s0W1VSsktYSOe9cFqNOIqmN1Gs8SmkjKZYFt017G4VwPxASInODuAdbTW7sXiFUf893RgA==",
            ["linux-x64"] = "sha512-EYdf2cNg7rgCWJnxCdJ+F3V39O8ihb37eHAu1LK8oAFizgTQbPOK7zHHXbPt8rX24COqODXeI3sIf0fCXG7H/A==",
            ["linux-musl-x64"] = "sha512-EYdf2cNg7rgCWJnxCdJ+F3V39O8ihb37eHAu1LK8oAFizgTQbPOK7zHHXbPt8rX24COqODXeI3sIf0fCXG7H/A==",
            ["linux-arm64"] = "sha512-Qh4eU4/y3yDjnfjjyPYihMj5/ODIlmt+Bzu17OI+fiSRDW57QmU5SiN63exPRNJPKUzcc1INa1NXdrJ+MqHjUQ==",
            ["linux-musl-arm64"] = "sha512-Qh4eU4/y3yDjnfjjyPYihMj5/ODIlmt+Bzu17OI+fiSRDW57QmU5SiN63exPRNJPKUzcc1INa1NXdrJ+MqHjUQ==",
            ["linux-arm"] = "sha512-gffT3xPz9sR7j/YJExkyPntrI0P2EP9XbOyWzth2/Gs0RstK+90RBcO0ncXoXy/beYll1SXw846Nf2zdnEz0QQ==",
            ["win-x64"] = "sha512-0BQ3HkAHHlKLSp1qRvf3SUhGpGsDuhB/jgFw75guyqbxJqEaS0Cw/VFO8i2nHglJUzQCRtMMR/IBAKE3ETMC4g==",
            ["win-arm64"] = "sha512-Gyl1Vy6OsWesLzmq+EP0Fb7b4Nid5232AvcA2SFcdYreldpNtYFFofPjnt62y9hQy7VTaZp65ICJjuAQRaVcIQ==",
        });

    /// <summary>
    /// tsrs, a port of the TypeScript 7 compiler and language server to Rust (https://github.com/oxc-project/tsrs), with
    /// the same protocol. Released for macOS on Apple silicon and Linux (glibc and musl, x64 and arm64).
    /// </summary>
    public static NpmTool Tsrs { get; } = new(
        "tsrs",
        "0.18.0-ts7.1.0-dev.20260929",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "@tsrs/darwin-arm64",
            ["linux-x64"] = "@tsrs/linux-x64-gnu",
            ["linux-musl-x64"] = "@tsrs/linux-x64-musl",
            ["linux-arm64"] = "@tsrs/linux-arm64-gnu",
            ["linux-musl-arm64"] = "@tsrs/linux-arm64-musl",
        },
        "package/tsrs",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "sha512-sAirMpaGA9uRQm2W+t4zC7PjnijNuFa4PCSIfLKKo0JyAUCRfnMeev0UGBQ9F3g+9Nz187tvikUs5Irh5+eajA==",
            ["linux-x64"] = "sha512-EdBwBgNICGu1r1h23ixdcKwsMo6E9ll7ZsY3mNERNYqE1Pwdtcat99d5E7zB1J+oy/Xk6YqiizaB6nolIjpDTw==",
            ["linux-musl-x64"] = "sha512-LHk+AOAtXuTlhJhOJ2+elriEIX17ycXljojeNxrrzckOipTG1LksoVWuCSas2nu00EnWAzrdKpt9oqPylUKZfQ==",
            ["linux-arm64"] = "sha512-zm9/z9yylig68JiZF4Aq0Cm26n09KEU8YoQRlG7tdDWTBNAIViYw8knxnWYsjMWVZ2XiU4mYq1xdrOKuOgABHg==",
            ["linux-musl-arm64"] = "sha512-Lw7VVU/YQ9JcarODICEiwzMLdFqZprqh5RGTgbS5Uc6I95P635/g5xA1pOC8RVs4efBvAR30J54iDG3l5JEYiw==",
        });

    /// <summary>
    /// Pagefind (https://pagefind.app), the static search index, its extended build (the one npm ships, with the
    /// segmentation of Chinese and Japanese), published as <c>@pagefind/&lt;os&gt;-&lt;arch&gt;</c>. A command step runs it
    /// with <c>tool: pagefind</c>.
    /// </summary>
    public static NpmTool Pagefind { get; } = new(
        "pagefind",
        "1.5.2",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "@pagefind/darwin-arm64",
            ["osx-x64"] = "@pagefind/darwin-x64",
            ["linux-x64"] = "@pagefind/linux-x64",
            ["linux-arm64"] = "@pagefind/linux-arm64",
            ["win-x64"] = "@pagefind/windows-x64",
            ["win-arm64"] = "@pagefind/windows-arm64",
        },
        "package/bin/pagefind_extended",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "sha512-MXpI+7HsAdPkvJ0gk9xj9g541BCqBZOBbdwj9g6lB5LCj6kSV6nqDSjzcAJwvOsfu0fjwvC8hQU+ecfhp+MpiQ==",
            ["osx-x64"] = "sha512-IojxFWMEJe0RQ7PQ3KXQsPIImNsbpPYpoZ+QUDrL8fAl/O27IX+LVLs74/UzEZy5uA2LD8Nz1AiwKr72vrkZQw==",
            ["linux-x64"] = "sha512-V+tFqHKXhQKq/WqPBD67AFy7scn1/aZID00ws4fSDd+1daSi5UHR9VVlRrOUYKxn3VuFQYRD7lYXdZK1WED1YA==",
            ["linux-arm64"] = "sha512-Ovt9+K35sqzn8H3ZMXGwls4TD/wMJuvRtShHIsmUQREmaxjrDEX7gHckRCrwYJ4XE1H1p6HkLz3wukrAnsfXQw==",
            ["win-x64"] = "sha512-Fa2Iyw7kaDRzGMfNYNUXNW2zbL5FQVDgSOcbDHdzBrDEdpqOqg8TcZ68F22ol6NJ9IGzvUdmeyZypLW5dyhqsg==",
            ["win-arm64"] = "sha512-hN9Nh90fNW61nNRCW9ZyQrAj/mD0eRvmJ8NlTUzkbuW8kIzGJUi3cxjFkEcMZ5h/8FsKWD/VcouZl4yo1F7B6g==",
        });

    /// <summary>
    /// Lightning CSS (https://lightningcss.dev), the CSS bundler, transpiler and minifier: its command line, published as
    /// <c>lightningcss-cli-&lt;os&gt;-&lt;arch&gt;</c>. A command step runs it with <c>tool: lightningcss</c>.
    /// </summary>
    public static NpmTool LightningCss { get; } = new(
        "lightningcss",
        "1.33.0",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "lightningcss-cli-darwin-arm64",
            ["osx-x64"] = "lightningcss-cli-darwin-x64",
            ["linux-x64"] = "lightningcss-cli-linux-x64-gnu",
            ["linux-musl-x64"] = "lightningcss-cli-linux-x64-musl",
            ["linux-arm64"] = "lightningcss-cli-linux-arm64-gnu",
            ["linux-musl-arm64"] = "lightningcss-cli-linux-arm64-musl",
            ["win-x64"] = "lightningcss-cli-win32-x64-msvc",
            ["win-arm64"] = "lightningcss-cli-win32-arm64-msvc",
        },
        "package/lightningcss",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "sha512-sso5hSFPis7ldw2FcBopsZviSVAXWRGX8ybUwMhQHssTlERenQJ88WihZs08tCdSnQULo2kunnrGhh4VjRw8Fg==",
            ["osx-x64"] = "sha512-/aDYpMv2QKpJoSIwvwpIs4tIX9SCU894eospSm55FR5MxDZkJDy4fEX6elsXkHCBDqbAknb4qd2h/oDMLTC4Ew==",
            ["linux-x64"] = "sha512-9Q4DAglm17bLhA+HZPB+vxjrHXdhR/8U44zyc8MQNA5Rw+tisvcoR/lv4iuBUFn2qreYVRWQq0wP0rgdhTSxyQ==",
            ["linux-musl-x64"] = "sha512-+EobHdqxQ21SUMF8OU0qcizNIrLpPDaPTKQCaGbra9gL3NrW3ELG+exZl5y+WzJsEjLZ0hhjbCFjHUL/03H2JA==",
            ["linux-arm64"] = "sha512-DkYmkBix3icAZKZsxGsjBAj59CIV0zflZtsT66lCPd2z3OqBLJlhKRrw4TlAW7mIkYeOEYWKVz/G00ZGXNse4A==",
            ["linux-musl-arm64"] = "sha512-euNOhzo1ysRl719HfFzRe9r9usWO2Z/Nb8RnmqXxb/kvzOgAv2BcflpoTu6tWhSrbHX6YS/WP6mMFrmII6T7Hw==",
            ["win-x64"] = "sha512-sgtNPw2gxnY8OzCCMTKCkHSIiBYb/wfXLpLMmDIJFzcX2OETY+VOnQd7OBHx+hVSlrMU7iWL6oJ5rc+nQwteVA==",
            ["win-arm64"] = "sha512-iwQ+8rHQy3ewC4c43Ik3UZ8TCC/rjShP3r/8TOPw4fT8wQmW7dS3BNNHPK+LWz9qNrObATJ4MZEEZVRneckOBw==",
        });

    /// <summary>
    /// The tools a command step can run with <c>tool:</c>, by name.
    /// </summary>
    public static IReadOnlyDictionary<string, NpmTool> Known { get; } = new Dictionary<string, NpmTool>(StringComparer.Ordinal)
    {
        ["lightningcss"] = LightningCss,
        ["pagefind"] = Pagefind,
    };

    /// <summary>
    /// The tool and version of a <c>tool:</c> option, <c>name</c> (its default version) or <c>name@version</c>, or null
    /// when Douze does not know the tool.
    /// </summary>
    public static (NpmTool Tool, string Version)? Parse(string option)
    {
        var at = option.IndexOf('@', StringComparison.Ordinal);
        var name = at < 0 ? option : option[..at];
        return Known.TryGetValue(name, out var tool) ? (tool, at < 0 ? tool.DefaultVersion : option[(at + 1)..]) : null;
    }

    /// <summary>
    /// The platform of the process, as a .NET runtime identifier: <c>osx-arm64</c>, <c>linux-x64</c>,
    /// <c>linux-musl-x64</c>, <c>win-x64</c>...
    /// </summary>
    public static string CurrentPlatform
    {
        get
        {
            var architecture = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                var other => other.ToString().ToLowerInvariant(),
            };

            if (OperatingSystem.IsMacOS())
            {
                return $"osx-{architecture}";
            }

            if (OperatingSystem.IsWindows())
            {
                return $"win-{architecture}";
            }

            if (OperatingSystem.IsLinux())
            {
                return IsMusl() ? $"linux-musl-{architecture}" : $"linux-{architecture}";
            }

            return $"{RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant()}-{architecture}";
        }
    }

    /// <summary>
    /// Whether the C library of this Linux system is musl (Alpine): its dynamic loader is <c>/lib/ld-musl-*</c>.
    /// </summary>
    private static bool IsMusl()
    {
        try
        {
            return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
