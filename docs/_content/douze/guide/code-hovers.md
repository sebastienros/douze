---
title: Code hovers
description: Type information and checked code samples for TypeScript, C# and Go fences, computed at build time with language servers.
---

The `hover-typescript`, `hover-csharp` and `hover-go` steps run a language server over the code fences of your pages,
as [twoslash](https://twoslash.netlify.app) does for TypeScript. Each step records the type of every symbol, the
errors and the queries of a fence, and the `highlight` step renders them as popups. A sample that does not compile
fails the build, so the documentation stays correct. This page shows how to set the steps up, what they need, and how
the results are cached.

## How it works

1. The `markdown` step writes each fence as `<pre data-lang="ts" data-meta="twoslash">`.
2. A hover step selects the fences of its languages, compiles all of them at once in generated projects, and adds a
   `data-twoslash` attribute to each one: twoslash's result shape, `{ "code": ..., "nodes": [...] }`, with the hovers
   (signature and documentation), errors, `^?` queries, highlights, completions and tags, with their positions. The
   step leaves the fence's code unchanged.
3. The `highlight` step reads that attribute and renders the popups in the highlighted code. Another highlighter can
   read it too, such as Expressive Code with a `twoslasher` that returns it.

All three steps produce the same shape, so one renderer shows them all. Chain the steps you need between `markdown`
and `highlight`:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  typescript:
    needs: markdown
    uses: hover-typescript
  csharp:
    needs: typescript
    uses: hover-csharp
  go:
    needs: csharp
    uses: hover-go
    with:
      errors: warn
  highlight:
    needs: go
    uses: highlight
    with:
      stylesheet: assets/code.css
  layout:
    needs: highlight
    uses: layout
  write:
    needs: layout
    uses: write
```

The stylesheet of `highlight` styles the popups (`twoslash-hover`, `twoslash-popup`, `twoslash-error`,
`twoslash-query`), which show on hover and on keyboard focus with CSS only.

## TypeScript

`hover-typescript` checks the fences of `ts`, `tsx` and `typescript` whose meta has the word `twoslash`:

````md
```ts twoslash
const greeting = "hello";
//    ^?
const length: number = greeting.length;
```
````

The `^?` line is a query: the type of the symbol above it is shown under the line. The step supports twoslash's
notations: `// @errors: 2322`, `// @noErrors` and the other handbook options, compiler options
(`// @strict: false`), `// ---cut---` and its variants, `// @filename: a.ts` for several files, and the custom tags
`annotate`, `log`, `warn` and `error`. `showEmit` is not supported.

The language server is TypeScript 7's native compiler. Its hover texts differ slightly from TypeScript 5's: union
members come in another order, and there is no `import x` line under an alias. The step adds the count of other
overloads at a call (`(+1 overload)`), as TypeScript 5 prints it.

Options for every snippet:

```yaml title="douze.yml"
typescript:
  needs: markdown
  uses: hover-typescript
  with:
    files: # placed next to every snippet
      "modules/sdk.mts": types/sdk.d.ts
    compiler:
      moduleResolution: bundler
      lib: [es2022, dom]
    handbook:
      noErrorValidation: true
```

`engine: tsrs` uses [tsrs](https://github.com/oxc-project/tsrs), a Rust port of TypeScript 7 with the same protocol,
for macOS on Apple silicon and Linux. It gives the same results with less memory.

## C# fences

`hover-csharp` checks the fences of `csharp`, `cs` and `c#` with the Roslyn language server, the one of the C#
extension of VS Code:

````md
```csharp title="Program.cs"
var list = new List<int> { 1, 2 };
Console.WriteLine(list.Count);
```
````

Every fence of those languages is checked, unless `trigger` names a word that the meta must contain. Snippets compile
in generated projects. A snippet can be top-level statements, type declarations or class members, and each one is
wrapped in its own namespace, so the snippets of a project don't see each other. `projects` describes the projects:

```yaml title="douze.yml"
csharp:
  needs: markdown
  uses: hover-csharp
  with:
    errors: warn
    projects:
      web:
        sdk: Microsoft.NET.Sdk.Web
        titles: [Program.cs]
        packages:
          Some.Package: "1.2.3"
        usings: [Some.Namespace]
```

A fence uses the project named by `project=name` in its meta, else the first project whose `titles` end the fence's
`title`, else the first whose `patterns` match its code, else `default`. A project also takes `framework` (`net10.0`),
`properties`, `files` (shared files such as stubs) and `context`: a class of a shared file whose fields are the
variables that fragments use without declaring them (a `builder` created by an earlier snippet).

## Go

`hover-go` checks the fences of `go` and `golang` with gopls:

````md
```go
import "fmt"

fmt.Println("hi")
```
````

Each snippet is a package of its own in a generated module. A snippet without a `package` clause gets
`package main`, and statements outside a function go in `func _() { ... }`. Imports are not added for you. `modules`
adds requirements, shared files, `titles` and `patterns`, as `projects` does for C#:

```yaml title="douze.yml"
go:
  needs: markdown
  uses: hover-go
  with:
    ignore: [UnusedVar]
    modules:
      default:
        requires:
          github.com/redis/go-redis/v9: v9.7.0
```

## Errors

A snippet with an error that it does not expect fails the build, with its page, the number of the fence on the page,
and its first line:

```
error: typescript: /index.md: twoslash fence 1 ('const greeting = "hello";'): errors were thrown in the sample, but not included in an error tag: 2322. Expected: // @errors: 2322. Compiler errors: [2322] index.ts:41 - Type 'number' is not assignable to type 'string'.
```

`errors: warn` makes it a warning, and the fence keeps its result, its errors rendered. That is the way to start on
existing documentation, where many C# and Go snippets are fragments. `ignore` (C# and Go) lists diagnostic codes that
are neither reported nor rendered, and `handbook: { noErrorValidation: true }` (TypeScript) renders unexpected errors
without a warning.

## Requirements and downloads

| Step | Needs | Downloads on first use |
|---|---|---|
| `hover-typescript` | nothing | the TypeScript 7 binary of the platform, from the npm registry |
| `hover-csharp` | the .NET SDK, 10 or later (`dotnet`, or `DOTNET_HOST_PATH`, or the `PATH`'s) | the Roslyn language server, about 65 MB, from nuget.org |
| `hover-go` | Go (`go` on the `PATH`, or the `go` option) | gopls, built with `go install` |

Tools are installed into `<cache>/tools/` (`.douze/tools/` by default) and verified against the integrity that Douze
pins for their default version. Later builds use them offline. `version` picks another release, `registry` a mirror
(TypeScript and C#), and `path` a binary you installed yourself.

The build checks the requirements before it starts. `douze doctor` lists them without building, and
`douze tools install` downloads the tools ahead, for a CI cache or an offline machine:

```sh
douze doctor
douze tools install
```

## Caching and cost

The fences of every page are checked in one pass: the snippets that share options are the files of one project,
checked by one language server that gets its requests in parallel. `processes` (4) language servers run at once, each
with a `timeout` (300 seconds).

Each outcome, errors included, is kept in the disk cache by its code and what the step depends on: the engine and its
version, the options and the content of shared files. A build checks only new or changed snippets, and starts no
language server when there is none. On a rebuild where nothing changed, the step takes no time at all. A cold C# check
costs a few seconds per project to start the server and load the project. See [Cache and incremental builds](cache.md).

## See also

- [hover-typescript step reference](../reference/steps/hover-typescript.md)
- [hover-csharp step reference](../reference/steps/hover-csharp.md)
- [hover-go step reference](../reference/steps/hover-go.md)
- [Syntax highlighting](syntax-highlighting.md)
- [Troubleshooting](troubleshooting.md)
