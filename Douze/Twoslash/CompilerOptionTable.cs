using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Douze.Twoslash;

/// <summary>
/// The compiler options of TypeScript 7 that a tsconfig accepts, by name (case insensitive, as twoslash matches
/// notations), with the kind of their value: what a <c>// @name: value</c> notation and the step's
/// <c>compiler</c> option may set. Enumerations are strings, as in a tsconfig.
/// </summary>
internal static class CompilerOptionTable
{
    public enum Kind { Boolean, Number, String, List, Object }

    /// <summary>
    /// A compiler option; <paramref name="Values"/> lists the values of an enumeration (lower case).
    /// </summary>
    public sealed record Option(string Name, Kind Kind, string[]? Values = null);

    private static readonly string[] Targets = ["es5", "es6", "es2015", "es2016", "es2017", "es2018", "es2019", "es2020", "es2021", "es2022", "es2023", "es2024", "es2025", "es2026", "esnext"];

    private static readonly FrozenDictionary<string, Option> Options = new Option[]
    {
        new("allowArbitraryExtensions", Kind.Boolean),
        new("allowImportingTsExtensions", Kind.Boolean),
        new("allowJs", Kind.Boolean),
        new("allowSyntheticDefaultImports", Kind.Boolean),
        new("allowUmdGlobalAccess", Kind.Boolean),
        new("allowUnreachableCode", Kind.Boolean),
        new("allowUnusedLabels", Kind.Boolean),
        new("alwaysStrict", Kind.Boolean),
        new("baseUrl", Kind.String),
        new("checkJs", Kind.Boolean),
        new("composite", Kind.Boolean),
        new("customConditions", Kind.List),
        new("declaration", Kind.Boolean),
        new("declarationDir", Kind.String),
        new("declarationMap", Kind.Boolean),
        new("disableReferencedProjectLoad", Kind.Boolean),
        new("disableSizeLimit", Kind.Boolean),
        new("disableSolutionSearching", Kind.Boolean),
        new("disableSourceOfProjectReferenceRedirect", Kind.Boolean),
        new("downlevelIteration", Kind.Boolean),
        new("emitBOM", Kind.Boolean),
        new("emitDeclarationOnly", Kind.Boolean),
        new("emitDecoratorMetadata", Kind.Boolean),
        new("erasableSyntaxOnly", Kind.Boolean),
        new("esModuleInterop", Kind.Boolean),
        new("exactOptionalPropertyTypes", Kind.Boolean),
        new("experimentalDecorators", Kind.Boolean),
        new("forceConsistentCasingInFileNames", Kind.Boolean),
        new("ignoreDeprecations", Kind.String),
        new("importHelpers", Kind.Boolean),
        new("incremental", Kind.Boolean),
        new("inlineSourceMap", Kind.Boolean),
        new("inlineSources", Kind.Boolean),
        new("isolatedDeclarations", Kind.Boolean),
        new("isolatedModules", Kind.Boolean),
        new("jsx", Kind.String, ["preserve", "react-native", "react-jsx", "react-jsxdev", "react"]),
        new("jsxFactory", Kind.String),
        new("jsxFragmentFactory", Kind.String),
        new("jsxImportSource", Kind.String),
        new("lib", Kind.List),
        new("libReplacement", Kind.Boolean),
        new("mapRoot", Kind.String),
        new("maxNodeModuleJsDepth", Kind.Number),
        new("module", Kind.String, ["commonjs", "amd", "system", "umd", "es6", "es2015", "es2020", "es2022", "esnext", "node16", "node18", "node20", "nodenext", "preserve"]),
        new("moduleDetection", Kind.String, ["auto", "legacy", "force"]),
        new("moduleResolution", Kind.String, ["node16", "nodenext", "bundler", "classic", "node", "node10"]),
        new("moduleSuffixes", Kind.List),
        new("newLine", Kind.String, ["crlf", "lf"]),
        new("noCheck", Kind.Boolean),
        new("noEmit", Kind.Boolean),
        new("noEmitHelpers", Kind.Boolean),
        new("noEmitOnError", Kind.Boolean),
        new("noErrorTruncation", Kind.Boolean),
        new("noFallthroughCasesInSwitch", Kind.Boolean),
        new("noImplicitAny", Kind.Boolean),
        new("noImplicitOverride", Kind.Boolean),
        new("noImplicitReturns", Kind.Boolean),
        new("noImplicitThis", Kind.Boolean),
        new("noLib", Kind.Boolean),
        new("noPropertyAccessFromIndexSignature", Kind.Boolean),
        new("noResolve", Kind.Boolean),
        new("noUncheckedIndexedAccess", Kind.Boolean),
        new("noUncheckedSideEffectImports", Kind.Boolean),
        new("noUnusedLocals", Kind.Boolean),
        new("noUnusedParameters", Kind.Boolean),
        new("outDir", Kind.String),
        new("outFile", Kind.String),
        new("paths", Kind.Object),
        new("plugins", Kind.List),
        new("preserveConstEnums", Kind.Boolean),
        new("preserveSymlinks", Kind.Boolean),
        new("reactNamespace", Kind.String),
        new("removeComments", Kind.Boolean),
        new("resolveJsonModule", Kind.Boolean),
        new("resolvePackageJsonExports", Kind.Boolean),
        new("resolvePackageJsonImports", Kind.Boolean),
        new("rewriteRelativeImportExtensions", Kind.Boolean),
        new("rootDir", Kind.String),
        new("rootDirs", Kind.List),
        new("skipDefaultLibCheck", Kind.Boolean),
        new("skipLibCheck", Kind.Boolean),
        new("sourceMap", Kind.Boolean),
        new("sourceRoot", Kind.String),
        new("stableTypeOrdering", Kind.Boolean),
        new("strict", Kind.Boolean),
        new("strictBindCallApply", Kind.Boolean),
        new("strictBuiltinIteratorReturn", Kind.Boolean),
        new("strictFunctionTypes", Kind.Boolean),
        new("strictNullChecks", Kind.Boolean),
        new("strictPropertyInitialization", Kind.Boolean),
        new("stripInternal", Kind.Boolean),
        new("target", Kind.String, Targets),
        new("tsBuildInfoFile", Kind.String),
        new("typeRoots", Kind.List),
        new("types", Kind.List),
        new("useDefineForClassFields", Kind.Boolean),
        new("useUnknownInCatchVariables", Kind.Boolean),
        new("verbatimModuleSyntax", Kind.Boolean),
    }.ToFrozenDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, [NotNullWhen(true)] out Option? option) => Options.TryGetValue(name, out option);

    public static IEnumerable<string> Names => Options.Values.Select(x => x.Name);
}
