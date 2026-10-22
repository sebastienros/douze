# twoslash with tsrs

The `twoslash` step (README, "twoslash") against the target site's twoslash pipeline: its code worker renders the
TypeScript fences with Expressive Code and its twoslash plugin, on TypeScript 6.0.3 in Node, with a 6.5 MB generated
declaration file that the snippets import. Measured on 2026-10-22, tsrs 0.18.0 (TypeScript 7.1.0-dev.20260929),
macOS arm64 (14 cores).

## Data set

The 397 twoslash fences of the target site's articles (96 pages, 354 distinct snippets): app host programs of 5 to 80
lines that import the SDK's declaration file; one has a `// ---cut---`, none has errors, queries or other notations.
Twoslash gives them 6,308 hovers. The fixtures of `TwoslashStepTests` cover the notations the data set does not use.

## Method

- Reference: the site's twoslash (`@ec-ts/twoslash` with the options of its Expressive Code configuration) on every
  snippet, its `{ code, nodes }` kept.
- Douze: the fences as Markdown pages, `markdown`, then `twoslash` with the same compiler options and the declaration
  file in `files`; the `data-twoslash` results compared node by node with the reference.
- Rendering: every fence rendered with the site's Expressive Code configuration three ways, with the plugin's
  `twoslasher` replaced through a Node module hook by one that returns precomputed results: the reference's results,
  the step's results, and the step's results with the reference's hover texts (which isolates the texts).

## Results

| | |
|---|---|
| Snippets checked | 354 of 354 |
| Hovers at the same positions (start, length, line, character, target) | 6,308 of 6,308, none missing, none extra |
| Code after the notations | 354 of 354 identical |
| Errors | none on either side |
| Hover texts, docs and tags identical | 4,676 of 6,308 (74.1%) |
| Popups identical as rendered (the plugin rewrites `(alias)` and `import` lines) | 5,396 of 6,308 (85.5%) |
| Fences rendered byte-identical, as is | 53 of 355 |
| Fences rendered byte-identical, with the reference's hover texts | 355 of 355 |
| Fences rendered byte-identical from the reference's results through the hook | 355 of 355 |

Every difference is TypeScript 7 printing quick info differently from TypeScript 5 and 6 (the hover of the TypeScript
7 language server is a new implementation); no difference comes from the step:

| Difference | Hovers | Rendered |
|---|---|---|
| At a call, the resolved signature without `(+N overloads)` | 837 | 874 |
| No `import x` line under an alias | 791 | 0 (the plugin removes the line) |
| At a call of an alias, `(alias) function f()` where TypeScript 5 prints `(alias) f()` | 352 | 0 (the plugin adds `function`) |
| A type and a const of the same name (a generated enum): the const's meaning first, and the const's documentation (none) instead of the type's | 71 | 33 |
| Union members in another order | 4 | 4 |
| `var Date: new () => Date` where TypeScript 5 prints `DateConstructor` and its overloads | 1 | 1 |

`(+N overloads)` could be restored with a signature help request per call (the count of signatures), if the
difference matters.

## Timings

TIMINGS

## Using it from an Expressive Code worker

The worker keeps Expressive Code and its twoslash plugin. It reads `data-twoslash` from the fence (the scanner of the
fences accepts the attribute), keeps the result by code, and the plugin's `twoslasher` returns it: the simplest is a
module hook that resolves `@ec-ts/twoslash` to a module whose `createTwoslasher()` returns
`(code) => structuredClone(results.get(code))` (what the comparison did, byte identical). The `typescript` package and
the twoslash compilations leave the worker; the step's `trim: true` checks the code the plugin receives (Expressive
Code removes trailing spaces and the blank lines around the code).
