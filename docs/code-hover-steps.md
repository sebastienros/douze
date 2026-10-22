# Code hover steps beyond TypeScript

The `twoslash` step gives TypeScript fences type hovers and build-time validation with the TypeScript 7 language
server. This note evaluates the same technique for C# and Go, records the tooling and its costs, and recommends how
to structure the steps. A `csharp` step is prototyped on top of a core shared with `twoslash`.

## The technique, language-neutral

What `twoslash` does is mostly not about TypeScript:

1. Find the selected fences of every page (`<pre data-lang data-meta>` as the `markdown` step writes them), trim them,
   key each by its code and the step's settings, and replay memoized outcomes from the disk cache.
2. Write the remaining snippets as the files of a few generated projects, start one language server per project, and
   pipeline all requests at once (hovers, pulled diagnostics; at most 512 in flight).
3. Map positions back to the snippet and write the result in twoslash's `{ code, nodes }` shape as a `data-twoslash`
   attribute, which one renderer shows for every language.
4. Report rejected snippets by page and fence: fail the build, or warn (`errors: warn`) and render the errors.

The refactor moves this into a core:

- `Douze/CodeHover/`: `LspConnection` (JSON-RPC over stdio, now also waits for a server notification), `LspClient`
  (initialize with hover, pulled diagnostics, completion and semantic token capabilities; hover, diagnostics,
  semantic tokens, open and close; the in-flight limit), `PositionConverter`, and the result format (`TwoslashResult`,
  `TwoslashNode`, `CodeOutcome`, `SharedFile`). An error node can carry a string code (`CS1061`), written as a string.
- `Douze/Workflow/Steps/CodeFenceStep.cs`: the fence scan, the memo flow, the error reporting, the attribute, the
  shared options (`errors`, `languages`, `tab-width`, `trim`, `processes`, `timeout`) and helpers (`files` options,
  tool folder, version probe). A step provides what selects a fence, what its key depends on, a `PrepareAsync` (settings
  key and state) and a `CheckAsync`.
- `Douze/Tools/`: `ToolInstaller` installs NuGet packages (`NuGetTool`) as well as npm ones: same folder layout, lock,
  pinned SHA-512 for the default version, verification, then zip extraction for a `.nupkg`.

The `twoslash` step keeps its options, notations and output: its tests are unchanged and green, and the Sample output
is identical. One behavior changed for every code step: an outcome with errors is memoized too, and reported again from
the memo by every build that uses it. Before, a snippet with errors was checked again by every build. That was cheap
for a few TypeScript fences, but costly for C# documentation, where most snippets are fragments with errors (see
below).

## C#: the Roslyn language server

### Tooling

- `roslyn-language-server` (the server of the C# extension of VS Code) is published on nuget.org as RID-specific
  `dotnet tool` packages: `roslyn-language-server.<rid>` for `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`,
  `linux-musl-x64`, `linux-musl-arm64`, `win-x64` and `win-arm64`; 63 to 67 MB each, 162 MB unpacked. `ToolInstaller`
  fetches it from the flat container (`https://api.nuget.org/v3-flatcontainer/`) with no `dotnet tool install`. The
  SHA-512 hashes of the pinned version's packages come from their nuget.org catalog entries and are pinned in
  `NuGetTool.RoslynLanguageServer`. Another version is verified against its catalog entry's hash, which only nuget.org
  gives, so a mirror (`registry`) serves the pinned version only. Nothing is embedded in Douze: a build that checks no C#
  fence, or whose outcomes are all memoized, does not download the server.
- The package is framework dependent (`net10.0`, `rollForward: Major`). Douze runs
  `dotnet Microsoft.CodeAnalysis.LanguageServer.dll --stdio --autoLoadProjects`. A runtime is enough to start it, but
  loading a project needs MSBuild, and so the .NET SDK, which restoring the snippets' packages needs too. The step
  fails with "requires the .NET SDK (10 or later)" when `dotnet --version` gives no SDK version. A C# documentation
  site's CI has the SDK anyway.
- Protocol notes: with `--autoLoadProjects`, the server loads the projects of the workspace folder and sends
  `workspace/projectInitializationComplete` when done. Pulled diagnostics (`textDocument/diagnostic`) are empty for a
  document that is not open: snippets are opened (`didOpen`), checked and closed, 64 at a time. Hovers are Markdown:
  a fenced signature, then documentation with hard breaks and `&nbsp;`. Diagnostics include IDE analyzers' (`IDE0130`,
  severity 3): only errors (severity 1) count and are rendered. Documentation comments of the snippets' own types only
  show in hovers when the project generates a documentation file.

### What a snippet needs

Documentation snippets are not files of a project. They are top-level statements, type declarations, members of a
class, or fragments that use the variables of an earlier snippet. Two things make them compile together:

- **One project per profile, not per snippet.** A generated `snippets.csproj` (`CSharpProject`): SDK
  (`Microsoft.NET.Sdk` by default, any SDK such as `Microsoft.NET.Sdk.Web`), target framework (`net10.0`), package
  references, global usings over the SDK's implicit usings, MSBuild properties, and shared files (stubs). It has empty
  `Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props` so that nothing of the site's
  folders changes it, no analyzers, and `NoWarn` for what the wrapping causes. `dotnet restore` runs first, with the
  `MSBuild*` variables of a parent MSBuild removed (`dotnet run` or `dotnet test` pass them on, which breaks the restore
  of packages).
- **Each snippet wrapped in a namespace of its own** (`CSharpSnippet`). A project can have only one file of top-level
  statements and one declaration of each type, so each snippet goes in `__Snippet<n>`:
  - top-level statements become the body of `async Task __Main(string[] args)`;
  - type declarations after them stay in the namespace;
  - declarations alone get the namespace, a file-scoped namespace becoming a block (`;` replaced by `{`, same length);
  - members without a type (an access modifier on something that is not a type) go in a class.

  A scanner of C# tokens finds the parts: comments, strings of every kind, character literals and brackets. C# orders
  them (usings, statements, declarations), so the first declaration at depth 0 starts them all. The wrapping only
  inserts text between parts, so offsets map both ways, and hovers drop the wrapper names (`__Snippet3.Greeter` is
  `Greeter`).
- **Fragments** use variables they don't declare (`builder.AddX(...)` after a snippet that created `builder`). A
  project's `context` class (from a shared file) is the base of the wrapper class, and its fields are those variables.
  A snippet's own local of the same name hides the field, which C# allows.
- **Which project:** the fence's `project=name`; else the project whose `titles` end the fence's `title`
  (`Program.cs`); else the project whose `patterns` match the code; else `default`.

The alternative, one project per snippet, gives exact top-level semantics, but loading costs about 0.13 s per project
(40 projects: 8.3 s against 3 s for one), or about 4.5 minutes for 2,000 snippets. Wrapping costs nothing at load.

### Costs

Measured on an Apple M-series machine (14 cores), Roslyn language server 5.13.0-1.26514.10, .NET SDK 11 RC 2:

| Case | Time | Notes |
| --- | --- | --- |
| Download of the server | a few seconds, once | 63 MB, into `<cache>/tools/roslyn/<version>/<rid>/` |
| Restore of a project of 42 packages from the local NuGet cache | 0.2 to 1.5 s | `dotnet restore`, per project |
| Server start and project load | about 3 s | per project |
| 404 snippets of one project | 12 s | 3 s load, 7 s diagnostics, 1 s for 6,400 hovers |
| The target site: 1,304 distinct C# fences, 2 projects | 27 to 29 s cold | 2 servers in parallel; 17,164 hovers on 1,313 fences |
| Rebuild, nothing changed | 0.2 s | the step's unit is replayed |
| Rebuild, one fence changed | 7.5 s | one snippet checked (restore, start); 961 memoized errors reported again |

Memory: by default the server uses the server GC on every core, about 2.9 GB for 400 snippets. With
`DOTNET_GCHeapCount=4` it uses 1.9 GB at the same speed, and with the workstation GC 0.65 GB but 60% slower. The step
sets 4 heaps (at most the number of cores) unless the variable is set.

### Validation on the target site

The target site has about 5,800 C# fences, 1,322 of them distinct in English, most titled after a file of the
site's application host or of a service. Checked with a host project (the SDK's hosting packages) and a web project
(its client packages):

- Without stubs or contexts, 1,204 of 1,304 snippets have errors. Most are fragments: 695 use a `builder` declared in
  an earlier snippet; 340 use the project types that the host SDK generates from project references; 136 are host
  code in fences without a title, checked in the web project.
- With a `context` class per project (declaring `builder`, `app`), a stub of the generated project types and
  `patterns` for the untitled host code, 961 still have errors. What is left is real:
  - documentation bugs, such as 194 calls of a generic `AddProject<T>()` without its name argument;
  - example types that the documentation assumes (`ExampleDbContext`, about 100);
  - packages not referenced (community extensions);
  - host fragments in untitled fences still checked in the web project;
  - experimental-API diagnostics that a snippet should suppress.

So checking every C# fence of the site is a migration of its own: start with `errors: warn` (or `ignore` codes), add
stubs and contexts, fix the documentation bugs it finds, then turn errors on. Hovers are useful from the start: 1,313
of the 1,323 fences get some, with signatures and documentation, errors or not.

### Identifier coverage

The `twoslash` step hovers every identifier that its own TypeScript scanner finds, as twoslash does, to stay identical
to it. For C# (and Go), the step asks the server for semantic tokens (`textDocument/semanticTokens/full`, one request
per document) and hovers the tokens of symbol types (namespace, type, class, method, property, field, variable,
parameter...). That costs one request per snippet. It needs no scanner per language, and it is exact: it includes
declarations and excludes keywords (`var`, `new`), literals and contextual keywords used as identifiers. Roslyn ignores
the client's token types; gopls filters its legend by them, so `LspClient` declares the protocol's types.

## Go: gopls

- No prebuilt binaries: `go install golang.org/x/tools/gopls@<version>` with a Go toolchain, into the tools folder
  (`GOBIN=<cache>/tools/gopls/<version>/`). The step would fail with "requires Go" when `go` is missing. gopls 0.24
  requires Go 1.27, which `go install` fetched by itself (`GOTOOLCHAIN=auto`): 18 s cold for the install, a 42 MB
  binary.
- Module setup: one module per profile (`go.mod` with `require`s for third-party imports, then `go mod download`),
  each snippet a package in its own folder (`s<n>/main.go`), so that `main` and its functions don't collide. A fragment
  without a `package` clause gets `package main` prepended (one line: positions shift by a line), and one without a
  function could be wrapped in `func _() { ... }` as C# statements are.
- Protocol: hovers are Markdown (a fenced signature, then documentation with links to the Go sources); pulled
  diagnostics work though gopls doesn't advertise them; semantic tokens need `initializationOptions:
  { "semanticTokens": true }` and the client's token types. First hover 2.6 s (standard library load), then
  milliseconds.
- The target site has about 160 Go fences (mostly `main.go`): a smaller case, and fragments as well.

## Recommendation: one step per language, on a shared core

A single step with per-language engines would share one option set. But the options and notations differ:

- `twoslash` has TypeScript compiler options, `// @` notations, queries, completions, cuts and handbook options, and
  must stay identical to twoslash.
- C# has projects (SDK, packages, usings, properties, contexts, titles, patterns), a restore, and diagnostic codes as
  strings.
- Go would have modules and requires.

The tools differ too: an npm tarball for TypeScript, a NuGet package plus an SDK for C#, `go install` plus a toolchain
for Go. A site enables each language separately, and each step memoizes by its own settings. What is shared is
everything around the server: the core above, and the output, which one renderer shows for every language.

So: `uses: twoslash` (TypeScript), `uses: csharp`, later `uses: go`, each a `CodeFenceStep` with its own engine. They
all write `data-twoslash`, which the site's highlighter renders whatever the language.

## The prototype: `uses: csharp`

```yaml
  csharp:
    needs: markdown
    uses: csharp
    with:
      errors: warn                      # or fail (default)
      ignore: [CS1591]                  # diagnostic codes neither reported nor rendered
      projects:
        host:
          sdk: Microsoft.NET.Sdk         # default
          framework: net10.0             # default
          titles: [Program.cs]           # fence titles (or their ends) that select it
          patterns: ['WebApplication\.'] # or code that selects it
          packages: { Some.Package: "1.2.3" }
          usings: [Some.Namespace]
          files: { Stubs.cs: data/csharp/stubs.cs, Context.cs: data/csharp/context.cs }
          context: SnippetContext        # the base class of top-level statements: ambient variables
      # version / path (the server), registry, dotnet, trigger, languages, processes, timeout, tab-width, trim
```

Every fence of `languages` (`csharp`, `cs`, `c#`) is checked, or only those whose meta has the `trigger` word. Tests:
`Douze.Tests/CodeHover/CSharpSnippetTests.cs` (wrapping, mapping, hover parsing) and
`Douze.Tests/Workflow/Steps/CSharpStepTests.cs` (hovers, isolation, errors that fail or warn, memoized errors,
packages, shared files, contexts, patterns, unknown projects), skipped without a .NET SDK or offline
(`DOUZE_TEST_ROSLYN` points them at an installed server).

## Follow-ups

- **Go step:** the same shape as `csharp`, with gopls installed by `go install`, a module per profile and fragment
  wrapping.
- **Site integration:** a `csharp` step in the site's workflow with host and web projects, stubs of the generated
  project types and contexts. The renderer accepts `data-twoslash` on C# fences (it already renders the shape) and
  shows string error codes.
- **Scale:** split a large project over several servers (`processes` only parallelizes projects today); reuse a
  restored project across builds (its folder is temporary, so each check restores, about 0.2 s from the NuGet cache).
- **Notations:** a C# `// ^?` query and `// @errors: CS1061` per snippet, if the site wants twoslash's notations in C#.
- **Key:** the outcome key has the server version and the project files, not the SDK version, which can change
  framework reference assemblies.
