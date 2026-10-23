---
title: Contribute to Douze
description: Build and test Douze, check the native AOT build, follow the repository conventions and work on the documentation.
liquid: false
---
Douze is developed on GitHub at [sebastienros/douze](https://github.com/sebastienros/douze). Issues and pull requests
are welcome. This page explains how to build and test Douze, how to check the native AOT build, and the conventions
the code follows.

Douze has no public release yet. Breaking changes are fine anywhere (APIs, configuration, front matter conventions,
folder layout, command line options): prefer the cleanest design, without compatibility shims, obsolete members or
migration code.

## Repository layout

| Folder | Contains |
|---|---|
| `Douze/` | The `douze` tool: command line, workflow engine, steps, Liquid, Markdown, highlighting, images |
| `Douze.Tests/` | The tests (xUnit v3) and their fixtures |
| `Douze.Tests.FakeTool/` | A small program the command step tests run |
| `Sample/` | A sample site that exercises most features |
| `bench/` | Benchmarks of large sites and image processing ([Benchmarks](guide/benchmarks.md)) |
| `docs/` | This documentation site, built by Douze, and design documents |
| `scripts/` | Maintenance scripts: the TextMate bundle, Sample output comparison, install script tests |
| `install.sh`, `install.ps1` | The install scripts ([Installation](guide/install.md)) |

## Build and test

Install the .NET SDK that `global.json` pins (`rollForward: latestFeature`, prereleases allowed), then, at the root of
the repository:

```bash
dotnet build
dotnet test
```

Tests use xUnit v3 on Microsoft.Testing.Platform, which `global.json` opts into. The build treats warnings as errors
and enforces the code style (`Common.props`).

Build the sample site with the working tree's Douze:

```bash
dotnet run --project Douze -- -s Sample -o site-out
```

`scripts/compare-sample.sh [base-ref]` builds the sample with the Douze of another ref (default `main`) and with the
working tree, and diffs the two outputs: run it when a change should not change the output.

## Check the native AOT build

Douze ships as a native AOT `dotnet tool`, and continuous integration smoke tests the AOT build of every pull request.
Publish it and build the sample with it:

```bash
dotnet publish Douze -c Release -o tool-out
./tool-out/douze -s Sample -o site-out
```

Anything based on reflection must keep working under AOT. For instance the `json` Liquid filter serializes through the
source-generated context in `Douze/Liquid/DouzeJsonContext.cs`: a new type that reaches it is added there.

## Conventions

- **Deterministic builds.** Documents are processed in parallel, so anything collected across documents is sorted
  before it is exposed. Two builds of the same sources produce the same output.
- **Collection order.** Items of `collections.all` and `collections.<tag>` are sorted by date ascending, using front
  matter `date` when it is defined, or `page.date` (the file date) otherwise, then by source path (ordinal), as
  Eleventy does.
- **Pagination.** `pagination.pages`, `page.next` and the other pagination links only link pages generated from the
  same template.
- **Dependencies.** Package versions are managed centrally in `Directory.Packages.props`, shared MSBuild properties
  live in `Common.props`. Keep dependencies on their latest stable versions.
- **Fluid previews.** Fluid comes from the 3.0 previews of the Feedz feed, declared in `NuGet.config` with package
  source mapping so that only `Fluid.*` packages come from it. Update to the latest preview when you touch Fluid.

`AGENTS.md` holds the same guidance for AI coding agents; `CLAUDE.md` imports it. Record new guidance in `AGENTS.md`.

## Continuous integration and releases

| Workflow | Runs on | Does |
|---|---|---|
| `pr.yml` | pull requests to `main` | tests, packs the native AOT tool for Linux x64, macOS arm64 and Windows x64, smoke tests it and the install scripts |
| `build.yml` | pushes to `main` | tests, then publishes framework dependent preview packages to Feedz |
| `publish.yml` | `v*` tags | publishes the native AOT tool packages of each platform, and a CoreCLR fallback, to NuGet |

Publishing to NuGet uses trusted publishing through the `release` environment: there is no NuGet API key secret.

## Work on the documentation

The documentation site is built by Douze itself, from `docs/`. `docs/README.md` has the writing rules. Serve it with
live reload, or build it to `docs/_site`:

```bash
douze serve -s docs   # http://localhost:8080/douze/
douze -s docs
```

- Match the voice of the repository README: plain, precise and short, sentence case headings, no em dashes.
- Take every option, default and message from the code in `Douze/`. When the README and the code disagree, the code
  wins.
- Build every example a reader can run with the current `main` before publishing it.
- Pages are rendered as Liquid before Markdown: a page that shows Liquid outside code fences sets `liquid: false` in
  its front matter, or wraps it in `raw`.
- Link to other pages with relative links to their `.md` file (`guide/collections.md`).

`AGENTS.md` requires a change to a feature to update the documentation in the same pull request.

## Update the bundled grammars and themes

The `highlight` step bundles the TextMate grammars of tm-grammars and the themes of tm-themes in
`Douze/Highlighting/textmate.zip`. `scripts/update-textmate.cs` writes it, at the versions and integrities pinned in the
script:

```bash
dotnet run scripts/update-textmate.cs
```

## See also

- [Installation](guide/install.md)
- [Workflow architecture](guide/workflow-architecture.md)
- [Benchmarks](guide/benchmarks.md)
- [Command line reference](reference/cli.md)
