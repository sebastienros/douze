# AGENTS.md

## Project status

Douze has **no public release yet**. There are no external consumers to protect, so breaking changes are fine anywhere: public APIs, configuration, front matter conventions, folder layout, CLI options. Prefer the cleanest design over backward compatibility, and don't add shims, obsolete members or migration code.

During the workflow engine refactor (`docs/workflow-engine-plan.md`), earlier decisions and code, including the plan's, can be changed or removed freely; update the plan where you deviate from it. Differences in the Sample output are allowed when they are intentional and documented in the change.

## Stack

- .NET 11 (`net11.0`), on the release candidate SDK pinned in `global.json` (`allowPrerelease`). Move to the stable .NET 11 SDK when it ships (November 2026): pin it in `global.json`, set `allowPrerelease` back to false, and take the stable `11.0.x` versions of `System.IO.Hashing` and `Microsoft.SourceLink.GitHub`.
- Runtime async is on for every project (`<Features>runtime-async=on</Features>` in `Common.props`), and `douze` runs with the non concurrent workstation GC (`ConcurrentGarbageCollection` in `Douze/Douze.csproj`): `bench/README.md` has the measurements behind both.
- Native AOT builds of `douze` use the Satori GC: `dotnet publish` and the RID-specific tool packages import the `PublishWithSatoriGC` MSBuild SDK (version pinned in `global.json`, `msbuild-sdks`; `UseSatoriGC` in `Douze/Douze.csproj`), which swaps the native AOT compiler and runtime for an experimental third party Satori build: 15 to 35% faster, more memory on large sites (`bench/README.md`, finding 9). `-p:UseSatoriGC=false` builds with the standard GC. `dotnet build`, `dotnet test` and the CoreCLR fallback package (`PublishAot=false`) never use it. Move its version with the SDK (a Satori build for the stable .NET 11 when it ships).
- Main dependencies: Fluid (Liquid templates), Markdig (Markdown), SharpYaml (YAML), Spectre.Console (live progress view), ZString.
- Fluid is on the 3.0 previews from the Feedz feed (`https://f.feedz.io/sebastienros/fluid/nuget/index.json`), declared in `NuGet.config` with package source mapping so only `Fluid.*` packages come from it. Update to the latest preview when touching Fluid, and move to the stable 3.0 on nuget.org once it ships.
- Fluid is the maintainer's own library: when a problem or a missing feature is better solved in Fluid (performance, fixes, features) than worked around in Douze, propose the Fluid change. Work in a separate git worktree of the Fluid repository cut from `origin/main` (in a scratchpad, or under `~/github/fluid/.claude/worktrees`), never in the `~/github/fluid` checkout itself, and add tests to Fluid's suite. Validate in Douze with a locally packed Fluid (`dotnet pack` with a version above the current preview) in a local folder used only for local runs, never committed. Commit on the Fluid branch, but don't push or open a Fluid PR: report the branch so the maintainer decides. Douze keeps building against the published preview until the change ships to Feedz.
- Package versions are managed centrally in `Directory.Packages.props`, shared MSBuild properties live in `Common.props`. Keep dependencies on their latest stable versions.
- Douze ships as a native AOT `dotnet tool` (`douze`). Anything reflection based must keep working under AOT; for instance the `json` Liquid filter relies on `Douze/Liquid/DouzeJsonContext.cs`.

## Architecture

Douze builds a site by running a workflow (`docs/workflow-engine-plan.md` is the design): a graph of steps that virtual files (path, content, metadata) flow through.

- `Douze/Cli/`: the command line (`CommandLine`, `BuildCommand`), `WorkflowSource` (finds the site options and the workflow), `ViewSelector`.
- `Douze/Workflow/`: the engine. `WorkflowGraph` validates a `WorkflowDefinition` and instantiates its steps; `SiteBuilder` runs it with the `Scheduling/Scheduler`, which streams files between steps by shape (`map`, `group`, `reduce`). `DefaultWorkflow` is the workflow of a site without a workflow file.
- `Douze/Workflow/Steps/`: the built-in steps (`uses:` ids `data`, `frontmatter`, `parse`, `markdown`, `headings`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `write`).
- `Douze/Workflow/Cache/`: the disk cache (`<source>/.douze/cache/`, `--no-cache`, `--cache-dir`). `BuildCache` stores the
  result of every unit by `ResultKey` (step, configuration, input refs) with its dependency set, in pack files
  (`PackFile`) under a binary index (`CacheIndex`); `NodeSerializer` writes metadata and nodes keeping their types. The
  scheduler replays a unit whose key is cached and whose dependencies did not change (`Scheduling/ReplayState`).
- `Douze/Workflow/Yaml/`: `douze.yml` loading. `YamlSource` keeps the position of every node, `WorkflowLoader` reads `site` (`SiteSettings`) and `steps` and reports every error with its line and column, `WorkflowSchema` is the field table shared by the loader and `--print-workflow`.
- `Douze/Workflow/Commands/`: `run:` steps in the modes `filter` (a process per file), `batch` (a process per input or group) and `worker` (one process, JSON lines over stdin and stdout, `WorkerProtocol`). `ProcessRunner` starts processes and kills their tree on timeout (`ProcessTree`, which avoids `Process.Kill(entireProcessTree: true)` on Unix: it can hang forever on macOS); `douze-worker.mjs` is the Node helper printed by `douze worker-helper`.
- `Douze/Markdown/`: Markdown rendering. `MarkdownOptions` is the `markdown:` section of `douze.yml`;
  `MarkdownPipelineFactory` builds the one Markdig pipeline of a build, which `MarkdownRenderer` (`BuildContext.Markdown`)
  renders with pooled renderers: anything that renders Markdown uses it, never a second configuration. Douze's own
  markup: raw regions (`RawRegionExtension`), asides (`AsideExtension`), code fences (`CodeFenceRenderer`), `.md`
  links (`LinkRewriter`). `HeadingScanner` and `Slugger` give the `headings` step the headings of rendered HTML.
- `Douze/Liquid/`: the Fluid bridge. `TemplateLoader` parses templates once per content hash and serves includes; `MetadataValue` is the model of every render.
  `DouzeParser` (the parser of a build, from `LiquidOptions.CreateParser`) exposes Fluid's protected argument and
  expression parsers to Douze's tags in `Tags/`: shortcodes (`ShortcodeRegistry`, scanned by `BuildContext.CreateAsync`
  before any template is parsed, `ShortcodeTag`, `ShortcodeModel`), `markdown`, `include_markdown` and `slot`
  (`MarkdownTags`), `error` and `warn` (`DiagnosticTags`). `FenceTable` protects the fences of Markdown pages in the
  `liquid` step.
- `Douze/Progress/`: the views of a build: `PlainProgressView` (a line per step), `SpectreProgressView` (live table and summary with a timeline), `SilentProgressView`; `StepSummary` formats what they share.
- `Douze/Tracing/`: `TraceRecorder` records build, step, command and (with `--trace-units`) unit spans; `ChromeTraceWriter` writes them for Perfetto (`--trace`), `TimelineHtmlWriter` into the self-contained `timeline.html` (`--timeline`).
- `Douze.Tests.FakeTool/`: the fake external tool of the command step tests, launched with `dotnet Douze.Tests.FakeTool.dll <mode>`; it is in `Douze.slnx` so that it builds in the configuration of the tests.
- `bench/`: the benchmark harness (`Douze.Bench`: a synthetic site, the pages of a real-world API reference) and its results (`bench/README.md`).

## Conventions

- Docs, comments and names don't name the external projects Douze is evaluated against: describe them generically (the target site, a real-world API reference data set).
- Builds must be deterministic: files are processed in parallel, so anything collected across files must be sorted before it is exposed (reduce and group inputs, collections, results).
- `VirtualFile` is immutable: a step returns new files (`With`, `Derived`) or the same instance. Metadata nodes are shared between files and never mutated once published: shallow clone (`Metadata.With`) and replace.
- Data is never copied into files. Folder and site data live in scopes, and steps read metadata only through `ScopeSet.TryResolve`, which records what a unit depends on: a unit is replayed from the cache only while every key it resolved beyond a file's own metadata resolves to the same value (or, for templates, to the same part of it: `ValueAccess`). A step that cannot record what it reads must not be incremental. `NodeHash` caches the hash of objects and arrays once computed: never change a node after it is published.
- Data files are keyed by file name: `_data/colors.json` is `colors`. The nearest scope wins: own data, folder data (nearest folder first), site data, the `site:` settings of `douze.yml` (`site.url`, whose `site` object is completed rather than hidden by a `site` data object), then the front matter of layouts.
- Collection items (`collections.all`, `collections.<tag>`) are sorted by date ascending, using front matter `date` if defined, or `page.date` (the file date) otherwise, then by path (ordinal). This matches Eleventy.
- Pagination pages (`pagination.pages`, `page.next`, ...) only link pages generated from the same template.
- Tags reach the build through the ambient values of the render (`AmbientKeys`: the step context, the page's model,
  the tracker, whether the output is Markdown, the page's fence table), never through statics. A tag that renders
  Markdown uses `TagRendering.RenderMarkdownAsync` (the site's pipeline), restores the page's fences in a body before
  (`TagRendering.RenderBodyAsync` does), and records the files it reads (`DependencyTracker.RecordTemplate`, absent
  files included). In a Markdown page, block HTML from a tag is written as a raw region
  (`RawRegionExtension.Encode`: one line, so that it stays in the tag's list item).
- Shortcode names are file names: lowercase letters, digits and underscores; a new built-in tag must be registered in
  `LiquidOptions.CreateParser`, so that a shortcode cannot hide it.

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

Compare the Sample output with another branch's Douze (lines with `Generated on` are ignored); any difference must be intentional and documented in the change:

```bash
scripts/compare-sample.sh <base-ref>
```

## Releases

- Every push to `main` publishes a framework dependent preview package to Feedz (`build.yml`).
- Pushing a `v*` tag publishes native AOT tool packages for each platform, plus a CoreCLR fallback, to NuGet (`publish.yml`). Publishing uses NuGet trusted publishing through the `release` environment, so there is no NuGet API key secret.
