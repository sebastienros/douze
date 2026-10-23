---
title: hover-go
description: "The hover-go step: hovers and errors of Go fences with gopls."
---

`uses: hover-go` checks the Go code fences of HTML pages with gopls and stores on each the hovers of its symbols
(signature and documentation) and its errors, in the shape of the
[`hover-typescript`](hover-typescript.md) step's results, so that the same renderer
([`highlight`](highlight.md)) shows them.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

Every fence of an HTML page that the `markdown` step wrote with a language of `languages` (`go`, `golang`), and, when
`trigger` is set, whose meta has that word, gets a `data-twoslash` attribute with twoslash's shape:
`{ "code": ..., "nodes": [...] }`, the hovers and the errors. The fence itself is unchanged, and the step sets no
metadata.

Each snippet is a package of its own in a generated module: `s<n>/main.go`, or the file name of the fence's `title`.
A snippet without a `package` clause gets `package main`, and statements outside a function go in `func _() { ... }`.
A fence uses the module of its meta's `module=name`, else the first module whose `titles` end its `title`, else the
first whose `patterns` match its code, else `default` (a module without requirements when `modules` does not declare
it).

Errors fail the build, naming the page, the fence's number on the page and its first line; with `errors: warn` they
are warnings, and the fence renders them. `ignore` lists diagnostic codes (`UnusedVar`, `UnusedImport`,
`BrokenImport`...) that are neither reported nor rendered.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `modules` | mapping of names to modules | `default`, a module without requirements | the generated modules, see below |
| `trigger` | string | none | a word the meta of a fence must have to be checked; every fence of `languages` when not set |
| `ignore` | string or list | none | diagnostic codes neither reported nor rendered |
| `version` | string | `v0.24.0` | the version of gopls to build (a leading `v` is added when missing) |
| `path` | string | none | a gopls executable, relative to the source folder, instead of building it |
| `go` | string | `go` on the `PATH` | the `go` executable that builds gopls, adds the requirements and that gopls runs, relative to the source folder |
| `languages` | string or list | `[go, golang]` | the fence languages the step checks |
| `errors` | string | `fail` | `fail`: a snippet with errors fails the build; `warn`: it is a warning |
| `tab-width` | integer, 0 to 16 | `0` | replace tabs with that many spaces first; `0` keeps them |
| `trim` | boolean | `true` | remove the spaces at the end of lines and the blank lines around the code first |
| `processes` | integer, 1 or more | the processor count, at most `4` | language servers at once |
| `timeout` | integer, 1 or more | `300` | seconds each language server has |

Each module of `modules`:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `requires` | mapping of module paths to versions | none | third-party modules the snippets import (`github.com/redis/go-redis/v9: v9.7.0`), added with `go get` |
| `files` | mapping of paths to paths | none | shared files of the source folder: a package of stubs at `api/api.go` is imported as `snippets/api`; `go.mod`, `go.sum` and the folders `s<n>` are reserved |
| `titles` | list | none | fence titles, or their ends (`main.go`), that select the module |
| `patterns` | list | none | regular expressions, one of which the code of a fence must match to select the module when no title matched |

## Requirements

- Go (`go`, or the `PATH`'s), which also adds the requirements and loads the packages.
- gopls. It has no prebuilt binaries: without `path`, on first use,
  `go install golang.org/x/tools/gopls@<version>` builds it into `<cache>/tools/gopls/<version>/`.
  `douze tools install` builds it ahead.

`douze doctor` checks both. A build that has no snippet to check starts no language server.

## Caching

Each fence's outcome, errors included, is kept in the cache by its language, its code, its module and file name, and
what the step depends on: gopls and its version (or the content of the `path` file), the Go version, the modules
(their fields and the content of their files), `ignore`, `tab-width` and `trim`. A build checks only new snippets.

The step is a reduce: it waits for every page it consumes before it checks them.

## Errors

```
douze.yml(7,3): error: option 'modules.m.files' of step 'go' has a reserved path 'go.mod': go.mod, go.sum and the folders s<n> are the module's and its snippets'
douze.yml(7,3): error: module 'm' of step 'go' has an unknown field 'require'; did you mean 'requires'?
douze.yml(7,3): error: option 'modules' of step 'go' must be a mapping of module names to modules
douze.yml(7,3): error: pattern '(' of module 'm' of step 'go' is not a regular expression: ...
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
  go:
    needs: markdown
    uses: hover-go
    with:
      errors: warn
      ignore: [UnusedVar]
      modules:
        default:
          requires:
            github.com/redis/go-redis/v9: v9.7.0
  highlight:
    needs: go
    uses: highlight
  write:
    needs: highlight
    uses: write
```

## Related

- [Code hovers guide](../../guide/code-hovers.md)
- [highlight](highlight.md), [hover-typescript](hover-typescript.md), [hover-csharp](hover-csharp.md)
- [`douze doctor` and `douze tools install`](../cli.md)
