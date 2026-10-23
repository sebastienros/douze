---
title: hover-csharp
description: "The hover-csharp step: hovers and errors of C# fences with the Roslyn language server."
---

`uses: hover-csharp` checks the C# code fences of HTML pages with the Roslyn language server and stores on each the
hovers of its symbols (signature and documentation) and its errors, in the shape of the
[`hover-typescript`](hover-typescript.md) step's results, so that the same renderer
([`highlight`](highlight.md)) shows them.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

Every fence of an HTML page that the `markdown` step wrote with a language of `languages` (`csharp`, `cs`, `c#`),
and, when `trigger` is set, whose meta has that word, gets a `data-twoslash` attribute with twoslash's shape:
`{ "code": ..., "nodes": [...] }`, the hovers and the errors. The fence itself is unchanged, and the step sets no
metadata.

Snippets compile in generated projects. A snippet can be top-level statements, type declarations or class members:
each is wrapped in a namespace of its own, so that the snippets of a project do not see each other. A fence uses the
project of its meta's `project=name`, else the first project whose `titles` end its `title`, else the first whose
`patterns` match its code, else `default` (an empty project when `projects` does not declare it).

A snippet with errors fails the build, naming its page, the fence's number on the page and its first line
(`/docs/a.md: csharp fence 1 ('var x = ...'): ...`); with `errors: warn` it is a warning, and the fence renders its
errors. `ignore` lists diagnostic codes that are neither reported nor rendered.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `projects` | mapping of names to projects | `default`, an empty project | the generated projects, see below |
| `trigger` | string | none | a word the meta of a fence must have to be checked; every fence of `languages` when not set |
| `ignore` | string or list | none | diagnostic codes neither reported nor rendered (`CS8321`) |
| `version` | string | `5.13.0-1.26514.10` | the version of the language server to download (`roslyn-language-server.<rid>`) |
| `path` | string | none | the language server's `Microsoft.CodeAnalysis.LanguageServer.dll`, relative to the source folder, instead of downloading it |
| `registry` | string | `https://api.nuget.org/v3-flatcontainer/` | the NuGet package content url (an http or https url) the server is downloaded from |
| `dotnet` | string | `DOTNET_HOST_PATH`, else `dotnet` on the `PATH` | the `dotnet` of the .NET SDK that restores the projects and runs the server, relative to the source folder |
| `languages` | string or list | `[csharp, cs, "c#"]` | the fence languages the step checks |
| `errors` | string | `fail` | `fail`: a snippet with errors fails the build; `warn`: it is a warning |
| `tab-width` | integer, 0 to 16 | `0` | replace tabs with that many spaces first; `0` keeps them |
| `trim` | boolean | `true` | remove the spaces at the end of lines and the blank lines around the code first |
| `processes` | integer, 1 or more | the processor count, at most `4` | language servers at once |
| `timeout` | integer, 1 or more | `300` | seconds each language server has |

Each project of `projects`:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `sdk` | string | `Microsoft.NET.Sdk` | the project SDK (`Microsoft.NET.Sdk.Web`) |
| `framework` | string | `net10.0` | the target framework |
| `packages` | mapping of ids to versions | none | package references (`Some.Package: "1.2.3"`) |
| `usings` | list | none | global usings, over the SDK's implicit ones |
| `properties` | mapping of names to values | none | MSBuild properties |
| `files` | mapping of paths to paths | none | shared files of the source folder (stubs that snippets use): a relative path in the project, without `.` or `..`, to a file of the source folder |
| `context` | string | none | a class, of a shared file, that statements run in: its fields are the variables that fragments use without declaring them |
| `titles` | list | none | fence titles, or their ends (`Program.cs`), that select the project |
| `patterns` | list | none | regular expressions, one of which the code of a fence must match to select the project when no title matched |

## Requirements

- The .NET SDK, 10 or later (`dotnet`, or `DOTNET_HOST_PATH`, or the `PATH`'s), which also restores the projects.
- The Roslyn language server. Without `path`, it is downloaded on first use from nuget.org (`registry`), the package
  of the current platform, into `<cache>/tools/roslyn/<version>/<rid>/`. `douze tools install` downloads it ahead.

`douze doctor` checks both. A build that has no snippet to check starts no language server.

## Caching

Each fence's outcome, errors included, is kept in the cache by its language, its code, its project and what the step
depends on: the language server and its version (or the content of the `path` file), the projects (their fields and
the content of their files), `ignore`, `tab-width` and `trim`. A build checks only new snippets.

The step is a reduce: it waits for every page it consumes before it checks them.

## Errors

```
douze.yml(6,3): error: project 'web' of step 'cs' has an unknown field 'packges'; did you mean 'packages'?
douze.yml(6,3): error: option 'projects' of step 'cs' must be a mapping of project names to projects
douze.yml(6,3): error: field 'usings' of project 'web' of step 'cs' must be a list of strings
douze.yml(6,3): error: pattern '(' of project 'web' of step 'cs' is not a regular expression: ...
douze.yml(6,3): error: option 'registry' of step 'cs' must be an http or https url
```

## Example

```yaml title="douze.yml"
site:
  content: docs

steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  markdown:
    needs: permalink
    uses: markdown
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
  highlight:
    needs: csharp
    uses: highlight
  write:
    needs: highlight
    uses: write
```

## Related

- [Code hovers guide](../../guide/code-hovers.md)
- [highlight](highlight.md), [hover-typescript](hover-typescript.md), [hover-go](hover-go.md)
- [`douze doctor` and `douze tools install`](../cli.md)
