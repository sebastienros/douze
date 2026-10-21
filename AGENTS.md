# AGENTS.md

## Project status

Douze has **no public release yet**. There are no external consumers to protect, so breaking changes are fine anywhere: public APIs, configuration, front matter conventions, folder layout, CLI options. Prefer the cleanest design over backward compatibility, and don't add shims, obsolete members or migration code.

During the workflow engine refactor (`docs/workflow-engine-plan.md`), earlier decisions and code, including the plan's, can be changed or removed freely; update the plan where you deviate from it. Differences in the Sample output are allowed when they are intentional and documented in the change.

## Stack

- .NET 10 (`net10.0`), SDK pinned in `global.json`.
- Main dependencies: Fluid (Liquid templates), Markdig (Markdown), SharpYaml (YAML), ZString.
- Fluid is on the 3.0 previews from the Feedz feed (`https://f.feedz.io/sebastienros/fluid/nuget/index.json`), declared in `NuGet.config` with package source mapping so only `Fluid.*` packages come from it. Update to the latest preview when touching Fluid, and move to the stable 3.0 on nuget.org once it ships.
- Package versions are managed centrally in `Directory.Packages.props`, shared MSBuild properties live in `Common.props`. Keep dependencies on their latest stable versions.
- Douze ships as a native AOT `dotnet tool` (`douze`). Anything reflection based must keep working under AOT; for instance the `json` Liquid filter relies on `Douze/Liquid/DouzeJsonContext.cs`.

## Conventions

- Builds must be deterministic: documents are processed in parallel, so anything collected across documents must be sorted before it is exposed.
- Collection items (`collections.all`, `collections.<tag>`) are sorted by date ascending, using front matter `date` if defined, or `page.date` (the file date) otherwise, then by source path (ordinal). This matches Eleventy.
- Pagination pages (`pagination.pages`, `page.next`, ...) only link pages generated from the same template.

## Build and test

```bash
dotnet build
dotnet test
```

Tests use xUnit v3 on Microsoft.Testing.Platform (opted in through `global.json`).

Check the native AOT build (CI smoke tests this on every PR):

```bash
dotnet publish Douze -c Release -o tool-out
./tool-out/douze -s Sample -o site-out
```

Generate the sample site:

```bash
dotnet run --project Douze -- -s Sample -o <output folder>
```

## Releases

- Every push to `main` publishes a framework dependent preview package to Feedz (`build.yml`).
- Pushing a `v*` tag publishes native AOT tool packages for each platform, plus a CoreCLR fallback, to NuGet (`publish.yml`). Publishing uses NuGet trusted publishing through the `release` environment, so there is no NuGet API key secret.
