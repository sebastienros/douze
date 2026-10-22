# Code hover steps beyond TypeScript

The `hover-typescript` step gives TypeScript fences type hovers and build-time validation with the TypeScript 7 language
server. This note evaluates the same technique for C# and Go, records the tooling and its costs, and recommends how
to structure the steps. A `hover-csharp` step and a `hover-go` step are prototyped on top of a core shared with `twoslash`.

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

- `Douze/CodeHover/`: `LspConnection` (JSON-RPC over stdio, now also waits for a server notification; an error answer
  is an `LspErrorException`, so that a step can ignore the error of one request), `LspClient`
  (initialize with hover, pulled diagnostics, completion and semantic token capabilities; hover, diagnostics,
  semantic tokens, open and close; the in-flight limit), `PositionConverter`, and the result format (`TwoslashResult`,
  `TwoslashNode`, `CodeOutcome`, `SharedFile`). An error node can carry a string code (`CS1061`), written as a string.
- `Douze/Workflow/Steps/CodeFenceStep.cs`: the fence scan, the memo flow, the error reporting, the attribute, the
  shared options (`errors`, `languages`, `tab-width`, `trim`, `processes`, `timeout`) and helpers (`files` options,
  tool folder, version probe). A step provides what selects a fence, what its key depends on, a `PrepareAsync` (settings
  key and state) and a `CheckAsync`.
- `Douze/Tools/`: `ToolInstaller` installs NuGet packages (`NuGetTool`) as well as npm ones: same folder layout, lock,
  pinned SHA-512 for the default version, verification, then zip extraction for a `.nupkg`. It also builds tools
  published as Go source (`GoTool`) with `go install`, under the same lock.

The `hover-typescript` step keeps its options, notations and output: its tests are unchanged and green, and the Sample output
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

The `hover-typescript` step hovers every identifier that its own TypeScript scanner finds, as twoslash does, to stay identical
to it. For C# (and Go), the step asks the server for semantic tokens (`textDocument/semanticTokens/full`, one request
per document) and hovers the tokens of symbol types (namespace, type, class, method, property, field, variable,
parameter...). That costs one request per snippet. It needs no scanner per language, and it is exact: it includes
declarations and excludes keywords (`var`, `new`), literals and contextual keywords used as identifiers. Roslyn ignores
the client's token types; gopls filters its legend by them, so `LspClient` declares the protocol's types.

## Go: gopls

### Tooling

- gopls has no prebuilt binaries. `ToolInstaller` runs `go install golang.org/x/tools/gopls@<version>` with
  `GOBIN=<cache>/tools/gopls/<version>/` (into a temporary folder, moved when done, under a lock file as for the other
  tools), so it needs a Go toolchain; the module proxy and checksum database of the Go environment verify what it
  downloads. gopls 0.24 requires Go 1.27 to build, which `go install` fetches by itself (`GOTOOLCHAIN=auto`, the
  default). The binary is 42 MB. `path` sets an installed gopls.
- The step fails with "requires Go" when `go env GOVERSION` cannot run (`go`, or the `PATH`'s), but only when it has
  snippets to check: memoized outcomes need no Go. The Go version is part of the outcomes' key (hovers and errors are
  those of its standard library), which costs one `go env` per build.
- gopls runs the `go` command to load packages: the `go` of the step goes first on its `PATH`. `GOWORK=off` and no
  `GOFLAGS`, so that a `go.work` of the folders above or flags meant for the site's own module (`-mod=vendor`) don't
  change the generated module.

### What a snippet needs

- **One module per profile** (`GoModule`, `modules` in the options, selected as C# projects are: `module=name`,
  `titles`, `patterns`, `default`): `go.mod` (`module snippets`, the `go` line of the Go version), the shared `files`
  (a package of stubs at `api/api.go` is imported as `snippets/api`), and `requires` (module paths to versions).
  `go get <module>@<version>...` adds the requirements and theirs; then `go mod download all` adds the checksums of
  every module of the build list. `go get` alone only adds those that the required modules' root packages need, and
  gopls cannot load `go.opentelemetry.io/otel/sdk/trace` without the checksums of its dependencies;
  `go mod download` without arguments adds none (only those of the `go.mod` files).
- **Each snippet a package of its own** (`GoSnippet`), in `s<n>/`, with the file name of its title (`main.go` by
  default), so that `main` and the snippets' declarations don't collide. A snippet with a `package` clause is a file as
  it is. Otherwise `package main` comes first (one line); statements (a fragment of a function body) go in
  `func _() { ... }`, the declarations before them (imports, types, constants, variables) left outside, and so are the
  function declarations after them. A scanner of Go's tokens (comments, interpreted and raw strings, runes, brackets)
  finds the parts: an item starts a line at depth 0, and its first word says what it is. Offsets map both ways.
- Not done: fragments that use an earlier snippet's variables or a package they don't import (`os`); goimports could
  add the imports, and a shared file could declare the variables, as the C# contexts do.

### Protocol notes

- Semantic tokens need `initializationOptions: { "semanticTokens": true }` and the client's token types (gopls filters
  its legend by them). `linksInHover: false` drops the links to pkg.go.dev, which are wrong for the snippets' own
  symbols (`pkg.go.dev/snippets/s0#Greeter`).
- Hovers are Markdown: a fenced signature (with `// size=16 (0x10)` at a declaration, dropped), `---`, then the
  documentation, whose doc links are `file://` urls to the Go sources (dropped, their text kept). The hover of a type
  adds its method set in a second fence, kept. The hover of a package is the whole package's documentation (fmt's is
  several pages, and every use of `fmt` has one): the step keeps its first paragraph. The blank identifier `_` is not
  hovered.
- Pulled diagnostics work (gopls doesn't advertise them). Errors are severity 1 with string codes (`UnusedVar`,
  `UndeclaredName`, `BrokenImport`, `IncompatibleAssign`); a syntax error has no code. Analyzers report warnings and
  hints, which are not rendered. Disabling every analyzer (`analyses`) makes gopls 0.24 panic.
- gopls answers the hover of the name of a package whose import it cannot load with an error ("no package data"),
  rather than nothing: such a hover is skipped. It loads the package of a file again when it opens with an import that
  it cannot resolve, so the step asks a snippet's diagnostics before its tokens and hovers.
- gopls makes a snapshot of the workspace for every change, an open or a close included, and type-checks the open
  packages for each. Opening the snippets 64 at a time and closing each when checked, as the C# step does, took 35 to
  48 s on the case below; opening them all at once and never closing them, 22 s. Snippets that are not open get the
  same diagnostics, but hovers were slower.

### Costs

Measured on an Apple M-series machine (14 cores), gopls 0.24.0, Go 1.26.2, native AOT `douze`, on the 77 Go fences of
the target site (67 distinct, 57 pages, mostly `main.go`):

| Case | Time | Notes |
| --- | --- | --- |
| `go install` of gopls | 10 s once (18 s with the Go 1.27 toolchain download) | fresh build cache, warm module cache |
| 67 snippets, standard library only | 7 s | 1 gopls; 0.6 to 0.7 GB; 63 of the 77 fences rejected, most for imports not required |
| `go get` and `go mod download all` of 39 modules | 77 s cold (53 s and 24 s), 1.3 s warm | network, then the module cache |
| 67 snippets with the 39 modules | 22 s | 1.5 to 1.9 GB; 1,902 hovers; 26 fences rejected (22 snippets) |
| Rebuild, nothing changed | 0.01 s | the step's unit is replayed |
| Rebuild, one fence changed | 2.4 s | one snippet checked (`go get`, start, load) |

The hovers cost most: 2,299 hover requests take 12 s of gopls time with the third-party modules (it parses the
dependencies' sources for their documentation) and raise its memory from 1.2 to 3 GB, while the diagnostics of all
the snippets take 2 s.

### Validation on the target site

With a `default` module requiring the 39 modules that the snippets import (at their latest versions), 22 of the 67
distinct snippets are rejected:

- fragments: variables declared and not used (15 errors, 5 snippets have no other: `ignore: [UnusedVar]`), names of an
  earlier snippet or packages not imported (`os`, 11 errors);
- API drift: functions and types that the required versions don't have (`azopenai.NewClient`, 10 errors), wrong
  argument or result counts (4); the site would pin the versions its documentation was written for;
- modules not required (3), and real bugs: statements at package level under a `package` clause (2).

Every snippet gets hovers. As for C#, checking the site is a migration: `errors: warn`, `requires` at the documented
versions, `ignore: [UnusedVar]` for fragments, then fixes.

## Recommendation: one step per language, on a shared core

A single step with per-language engines would share one option set. But the options and notations differ:

- `twoslash` has TypeScript compiler options, `// @` notations, queries, completions, cuts and handbook options, and
  must stay identical to twoslash.
- C# has projects (SDK, packages, usings, properties, contexts, titles, patterns), a restore, and diagnostic codes as
  strings.
- Go has modules and requires.

The tools differ too: an npm tarball for TypeScript, a NuGet package plus an SDK for C#, `go install` plus a toolchain
for Go. Even the protocol use differs: the C# step opens 64 snippets at a time, the Go step all of them at once. A
site enables each language separately, and each step memoizes by its own settings. What is shared is everything around
the server: the core above, and the output, which one renderer shows for every language.

So: `uses: twoslash` (TypeScript), `uses: hover-csharp`, `uses: go`, each a `CodeFenceStep` with its own engine. They
all write `data-twoslash`, which the site's highlighter renders whatever the language.

## The prototype: `uses: hover-csharp`

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

## The prototype: `uses: go`

```yaml
  go:
    needs: markdown
    uses: go
    with:
      errors: warn                        # or fail (default)
      ignore: [UnusedVar]                 # diagnostic codes neither reported nor rendered
      modules:
        default:
          requires: { github.com/redis/go-redis/v9: v9.7.0 }  # third-party imports, added with go get
          files: { api/api.go: data/go/api.go }               # shared files: import "snippets/api"
          titles: [main.go]                                   # fence titles (or their ends) that select it
          patterns: ['"net/http"']                            # or code that selects it
      # version / path (gopls), go, trigger, languages, processes, timeout, tab-width, trim
```

Every fence of `languages` (`go`, `golang`) is checked, or only those whose meta has the `trigger` word. Tests:
`Douze.Tests/CodeHover/GoSnippetTests.cs` (completion, mapping, hover parsing) and
`Douze.Tests/Workflow/Steps/GoStepTests.cs` (hovers, isolation, errors that fail or warn, memoized errors, ignored
codes, requires and shared files, unknown modules, a missing Go), skipped without Go or offline (`DOUZE_TEST_GOPLS`
points them at an installed gopls).

## Follow-ups

- **Site integration:** a `hover-csharp` step in the site's workflow with host and web projects, stubs of the generated
  project types and contexts; a `hover-go` step with the `requires` of the documented versions. The renderer accepts
  `data-twoslash` on C# and Go fences (it already renders the shape) and shows string error codes.
- **Go fragments:** add the missing imports (goimports) and declare an earlier snippet's variables (a shared file), as
  the C# contexts do.
- **Scale:** split a large project over several servers (`processes` only parallelizes projects today); reuse a
  restored project across builds (its folder is temporary, so each check restores, about 0.2 s from the NuGet cache).
- **Notations:** a C# `// ^?` query and `// @errors: CS1061` per snippet, if the site wants twoslash's notations in C#.
- **Key:** the outcome key has the server version and the project files, not the SDK version, which can change
  framework reference assemblies. The Go step's key has the Go version.
