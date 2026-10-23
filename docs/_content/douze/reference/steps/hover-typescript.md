---
title: hover-typescript
description: "The hover-typescript step: twoslash type information for TypeScript fences with TypeScript 7."
---

`uses: hover-typescript` computes the type information of TypeScript code fences, as
[twoslash](https://twoslash.netlify.app) does, without Node: it runs the language server of TypeScript 7 (Microsoft's
native compiler), which Douze downloads, and stores the result on each fence for the
[`highlight`](highlight.md) step, or another highlighter, to render as popups.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

Every fence of an HTML page that the `markdown` step wrote as `<pre data-lang="ts" data-meta="...">`, with a language
of `languages` (`ts`, `tsx`, `typescript`) and the word `twoslash` in its meta (` ```ts twoslash `), gets a
`data-twoslash` attribute: twoslash's result for its code, `{ "code": ..., "nodes": [...] }`. `code` is the code
without its notations; `nodes` are the hovers, `^?` queries, errors, `^^^` highlights, `^|` completions and custom
tags, with their offsets, lines and characters. The fence itself is unchanged, and the step sets no metadata.

Before it is checked, the code is prepared as highlighters render it: tabs replaced (`tab-width`), and with `trim`,
the spaces at the end of lines and the blank lines around the code removed.

Notations:

- handbook options: `// @errors: 2322`, `// @noErrors`, `// @noErrorsCutted`, `// @noErrorValidation`,
  `// @noStaticSemanticInfo`, `// @keepNotations`; `showEmit` and `showEmittedFile` are not supported;
- compiler options: `// @strict: false`, `// @target: es2015`;
- `// ---cut---` (or `---cut-before---`), `// ---cut-after---`, `// ---cut-start---` and `// ---cut-end---`;
- `// @filename: a.ts`, which starts another file of the snippet;
- the custom tags of `tags` (`// @log: text`).

All the snippets of a build are checked at once: the snippets that share their compiler options are the files of
one project (each in its own folder, with the `files` next to it), checked by one language server whose requests are
sent in parallel, and up to `processes` language servers run at once.

A snippet that twoslash would reject (an error that its `// @errors` does not list, a query that finds nothing)
fails the build, naming its page, the fence's number on the page and its first line:

```
error: typescript: /docs/a.md: twoslash fence 2 ('const x: string = 1'): ...
```

With `errors: warn` it is a warning, and the fence keeps its result when it has one (its errors are rendered).
`handbook: { noErrorValidation: true }` renders unexpected errors without a warning.

TypeScript 7 prints some types differently from TypeScript 5 (no `import x` line under an alias, union members in
another order). At a call, the step adds the count of the other overloads after the signature (`(+1 overload)`), as
TypeScript 5 prints it.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `engine` | string | `typescript` | `typescript` (TypeScript 7, `@typescript/typescript-<platform>`, for Windows, macOS and Linux) or `tsrs` ([tsrs](https://github.com/oxc-project/tsrs), a port of TypeScript 7 to Rust with the same protocol, for macOS on Apple silicon and Linux) |
| `version` | string | `7.0.2` for `typescript`, `0.18.0-ts7.1.0-dev.20260929` for `tsrs` | the version of the engine to download |
| `path` | string | none | the engine's binary, relative to the source folder, instead of downloading it |
| `registry` | string | `https://registry.npmjs.org/` | the npm registry (an http or https url) the engine is downloaded from |
| `compiler` | mapping | twoslash's defaults | tsconfig compiler options over `strict: true`, `module: esnext`, `target: esnext`, `allowJs: true`, `skipDefaultLibCheck: true`, `skipLibCheck: true`, `moduleDetection: force` |
| `files` | mapping of paths to paths | none | files placed next to every snippet (`"modules/sdk.d.ts": types/sdk.d.ts`): a relative path without `.` or `..`, to a file of the source folder |
| `tags` | string or list | `[annotate, log, warn, error]` | the custom tags (`// @log: text`) |
| `handbook` | mapping | none | handbook options for every snippet: `errors`, `noErrors`, `noErrorsCutted`, `noErrorValidation`, `noStaticSemanticInfo`, `keepNotations` |
| `languages` | string or list | `[ts, tsx, typescript]` | the fence languages the step checks |
| `errors` | string | `fail` | `fail`: a rejected snippet fails the build; `warn`: it is a warning |
| `tab-width` | integer, 0 to 16 | `0` | replace tabs with that many spaces first; `0` keeps them |
| `trim` | boolean | `true` | remove the spaces at the end of lines and the blank lines around the code first |
| `processes` | integer, 1 or more | the processor count, at most `4` | language servers at once |
| `timeout` | integer, 1 or more | `300` | seconds each language server has |

## Requirements

The engine. Without `path`, it is downloaded on first use from the npm registry, the package of the current
platform, verified against the integrity Douze pins for the default version (other versions against the integrity
the registry gives), into `<cache>/tools/<engine>/<version>/<platform>/`. Later builds use it offline, and
`douze tools install` downloads it ahead. `douze doctor` reports it as `download` until then. A build that has no
snippet to check (every result in the cache) starts no language server and needs no engine.

## Caching

Each fence's outcome, errors included, is kept in the cache by its language, its code and what the step depends on:
the engine and its version (or the content of the `path` binary), the compiler options, the content of `files`, the
tags, the handbook options, `tab-width` and `trim`. A build checks only new snippets. A cached error is reported
again by every build that uses it.

The step is a reduce: it waits for every page it consumes before it checks them.

## Errors

```
douze.yml(5,3): error: option 'engine' of step 'ts' must be 'typescript' or 'tsrs', not 'tsc'
douze.yml(5,3): error: option 'compiler' of step 'ts' has an unknown compiler option 'strct'; did you mean 'strict'?
douze.yml(5,3): error: option 'compiler' of step 'ts' has an invalid 'module': ...
douze.yml(5,3): error: option 'handbook' of step 'ts' has an unknown option '...'
douze.yml(5,3): error: option 'registry' of step 'ts' must be an http or https url
douze.yml(5,3): error: option 'errors' of step 'ts' must be 'fail' or 'warn', not '...'
douze.yml(5,3): error: options 'tab-width' (0 to 16), 'processes' and 'timeout' (at least 1) of step 'ts' are out of range
```

During the build: `step '...': the file '...' of option 'files' does not exist`.

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
  typescript:
    needs: markdown
    uses: hover-typescript
    with:
      files:
        "modules/sdk.d.ts": types/sdk.d.ts
      compiler:
        moduleResolution: bundler
        lib: [es2022, dom]
      handbook:
        noErrorValidation: true
  highlight:
    needs: typescript
    uses: highlight
  write:
    needs: highlight
    uses: write
```

## Related

- [Code hovers guide](../../guide/code-hovers.md)
- [highlight](highlight.md), [hover-csharp](hover-csharp.md), [hover-go](hover-go.md)
- [`douze doctor` and `douze tools install`](../cli.md)
- [Cache and incremental builds](../../guide/cache.md)
