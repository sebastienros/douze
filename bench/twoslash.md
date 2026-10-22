# twoslash with TypeScript 7

The `twoslash` step (README, "twoslash") against the target site's twoslash pipeline: its code worker renders the
TypeScript fences with Expressive Code and its twoslash plugin, on TypeScript 6.0.3 in Node, with a 6.5 MB generated
declaration file that the snippets import. Measured on 2026-10-22 on macOS arm64 (14 cores), with TypeScript 7.0.2
(the step's default engine), TypeScript 7.1.0-beta and tsrs 0.18.0 (TypeScript 7.1.0-dev.20260929).

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

## Engines

| | TypeScript 7 (`tsc --lsp --stdio`) | tsrs (`tsrs --lsp -stdio`) |
|---|---|---|
| Publisher, status | Microsoft, stable (7.0.2; 7.1 in beta) | a port to Rust, pre-1.0 (0.18.0) |
| npm packages | `@typescript/typescript-<os>-<arch>`: Windows, macOS (arm64, x64), Linux (x64, arm64, arm), others | `@tsrs/<os>-<arch>[-<libc>]`: macOS arm64, Linux x64 and arm64 (glibc, musl) |
| Download | 26 to 29 MB unpacked, the binary is static | 21 MB unpacked |
| Hover, diagnostics, completions | the protocol tsrs ports | the same protocol |
| Results on the data set | identical to tsrs (397 of 397 fences, 7.0.2 and 7.1.0-beta) | identical |
| All 354 snippets, language server alone | 0.30 to 0.63 s (7.0.2), 0.21 to 0.22 s (7.1.0-beta) | 0.32 to 0.37 s |
| Peak memory of the language server | 245 to 290 MB | 155 MB |

**Choice: TypeScript 7 by default, tsrs as `engine: tsrs`.** The results are the same, and so is the speed at this
size; TypeScript 7 is the official, stable compiler, and is published for every platform Douze ships on, Windows and
Intel macOS included, where tsrs has no binary. tsrs uses less memory, which does not matter for a few hundred
snippets. The client is the same for both (the language server protocol of TypeScript 7, which tsrs mirrors): the
engine is a value of the step's configuration.

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

Three runs each, the native AOT build of Douze; the Node numbers are the site's twoslash with the options of its
configuration, in a new process each time (the worker starts one per build).

| | Node, TypeScript 6.0.3 | Douze, TypeScript 7.0.2 | Douze, tsrs 0.18.0 |
|---|---|---|---|
| All 354 snippets (cold) | 2.60 to 2.76 s, 2.9 to 3.0 s with Node's start | 0.36 to 0.47 s (step 0.45 to 0.56 s) | 0.35 to 0.38 s (step 0.46 to 0.47 s) |
| First build, with the engine's download | | 0.44 s, step 1.09 s with the download | |
| Nothing changed | per-fence cache of the worker | 0 (the step's unit is replayed) | 0 |
| One fence edited | a Node process, then 0.33 s for the first snippet (the declaration file) | 0.13 to 0.15 s (the language server's start, the declaration file) | |

The worker's twoslash runs one snippet at a time; the language server answers the hovers of all the snippets in
parallel. Rendering the 355 fences with Expressive Code took 5.9 to 14.8 s with twoslash and 3.2 to 7.6 s with the
precomputed results (on a loaded machine: other builds were running).

## Using it from an Expressive Code worker

The worker keeps Expressive Code and its twoslash plugin. It reads `data-twoslash` from the fence (the scanner of the
fences accepts the attribute), keeps the result by code, and the plugin's `twoslasher` returns it: the simplest is a
module hook that resolves `@ec-ts/twoslash` to a module whose `createTwoslasher()` returns
`(code) => structuredClone(results.get(code))` (what the comparison did, byte identical). The `typescript` package and
the twoslash compilations leave the worker; the step's `trim: true` checks the code the plugin receives (Expressive
Code removes trailing spaces and the blank lines around the code).
