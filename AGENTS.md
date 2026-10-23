# AGENTS.md

## Project status

Douze has **no public release yet**. There are no external consumers to protect, so breaking changes are fine anywhere: public APIs, configuration, front matter conventions, folder layout, CLI options. Prefer the cleanest design over backward compatibility, and don't add shims, obsolete members or migration code.

During the workflow engine refactor (`docs/workflow-engine-plan.md`), earlier decisions and code, including the plan's, can be changed or removed freely; update the plan where you deviate from it. Differences in the Sample output are allowed when they are intentional and documented in the change.

## Stack

- .NET 11 (`net11.0`), on the release candidate SDK pinned in `global.json` (`allowPrerelease`). Move to the stable .NET 11 SDK when it ships (November 2026): pin it in `global.json`, set `allowPrerelease` back to false, and take the stable `11.0.x` versions of `System.IO.Hashing` and `Microsoft.SourceLink.GitHub`.
- Runtime async is on for every project (`<Features>runtime-async=on</Features>` in `Common.props`), and `douze` runs with the non concurrent workstation GC (`ConcurrentGarbageCollection` in `Douze/Douze.csproj`): `bench/README.md` has the measurements behind both.
- Native AOT builds of `douze` use the Satori GC: `dotnet publish` and the RID-specific tool packages import the `PublishWithSatoriGC` MSBuild SDK (version pinned in `global.json`, `msbuild-sdks`; `UseSatoriGC` in `Douze/Douze.csproj`), which swaps the native AOT compiler and runtime for an experimental third party Satori build: 15 to 35% faster, more memory on large sites (`bench/README.md`, finding 9). `-p:UseSatoriGC=false` builds with the standard GC. `dotnet build`, `dotnet test` and the CoreCLR fallback package (`PublishAot=false`) never use it. Move its version with the SDK (a Satori build for the stable .NET 11 when it ships).
- Main dependencies: Fluid (Liquid templates), Markdig (Markdown), SharpYaml (YAML), Spectre.Console (live progress view), ZString, NetVips (libvips, the image features).
- NetVips binds libvips with `DllImport`s (AOT safe); the native library comes from one `NetVips.Native.<rid>` package per runtime identifier of `ToolPackageRuntimeIdentifiers`, referenced in `Douze/Douze.csproj` for the RID being built (and for the SDK's RID in builds and tests without one), so a native AOT publish and each RID tool package carry `libvips` next to the binary. The CoreCLR fallback package (`any`) references all of them, and carries each under `runtimes/<rid>/native`, where the host finds the one of the machine. Elsewhere (a build from source on another platform), NetVips' module initializer records the missing library at startup, and only the units that process images fail, with the install hint (`ImageProcessor.MissingLibraryMessage`). Keep the `NetVips.Native.*` versions in step in `Directory.Packages.props`; adding a RID to the tool packages means adding its native package, in both item groups of `Douze/Douze.csproj`.
- Fluid is on the 3.0 previews from the Feedz feed (`https://f.feedz.io/sebastienros/fluid/nuget/index.json`), declared in `NuGet.config` with package source mapping so only `Fluid.*` packages come from it. Update to the latest preview when touching Fluid, and move to the stable 3.0 on nuget.org once it ships.
- Fluid is the maintainer's own library: when a problem or a missing feature is better solved in Fluid (performance, fixes, features) than worked around in Douze, propose the Fluid change. Work in a separate git worktree of the Fluid repository cut from `origin/main` (in a scratchpad, or under `~/github/fluid/.claude/worktrees`), never in the `~/github/fluid` checkout itself, and add tests to Fluid's suite. Validate in Douze with a locally packed Fluid (`dotnet pack` with a version above the current preview) in a local folder used only for local runs, never committed. Commit on the Fluid branch, but don't push or open a Fluid PR: report the branch so the maintainer decides. Douze keeps building against the published preview until the change ships to Feedz.
- Package versions are managed centrally in `Directory.Packages.props`, shared MSBuild properties live in `Common.props`. Keep dependencies on their latest stable versions.
- Douze ships as a native AOT `dotnet tool` (`douze`). Anything reflection based must keep working under AOT; for instance the `json` Liquid filter relies on `Douze/Liquid/DouzeJsonContext.cs`.

## Architecture

Douze builds a site by running a workflow (`docs/workflow-engine-plan.md` is the design): a graph of steps that virtual files (path, content, metadata) flow through.

- `Douze/Cli/`: the command line (`CommandLine`, `BuildCommand`), `WorkflowSource` (finds the site options and the workflow), `ViewSelector`.
- `Douze/Workflow/`: the engine. `WorkflowGraph` validates a `WorkflowDefinition` and instantiates its steps; `SiteBuilder` runs it with the `Scheduling/Scheduler`, which streams files between steps by shape (`map`, `group`, `reduce`). `DefaultWorkflow` is the workflow of a site without a workflow file.
- `Douze/Workflow/Steps/`: the built-in steps (`uses:` ids `data`, `frontmatter`, `parse`, `markdown`, `headings`, `paginate`, `permalink`, `collect`, `localize`, `replace`, `fork`, `liquid`, `layout`, `check-code`, `links`, `check-links`, `write`). `links` extracts links and anchors with `Markdown/LinkScanner`; `check-links` is a metadata-only reduce that emits nothing, a branch `write` does not wait for (`docs/site-features-plan.md` 3.11).
- `Douze/Workflow/LocaleSet.cs`: `site.locales` (`BuildContext.Locales`, `LocaleSet.None` without the section), also exposed to templates as `site.locales` in the settings scope.
- `Douze/Workflow/Steps/`: the built-in steps (`uses:` ids `data`, `frontmatter`, `parse`, `markdown`, `headings`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `images`, `image-map`, `pictures`, `social-image`, `write`). A step or a tag that produces a file besides what its unit returns (an image variant) adds it with `StepContext.AddOutput`: it is stored and replayed with the unit, and `write` accepts identical files (same `FileRef`) for one path, written once.
- `Douze/Cli/`: the command line (`CommandLine`, `BuildCommand`, `WatchCommand` for `watch` and `serve`, `DoctorCommand`, `ToolsCommand` for `tools install`), `WorkflowSource` (finds the site options and the workflow), `ViewSelector`.
- `Douze/Workflow/`: the engine. `WorkflowGraph` validates a `WorkflowDefinition` and instantiates its steps; `SiteBuilder` runs it with the `Scheduling/Scheduler`, which streams files between steps by shape (`map`, `group`, `reduce`), from a `SourceSnapshot` of the source folder. `DefaultWorkflow` is the workflow of a site without a workflow file. Steps declare what they need outside Douze (`Step.Requirements`: libvips, a downloaded tool, the executable of a command; `Requirement.cs`): `SiteBuilder` checks them before any unit runs and fails the build when one is missing, `douze doctor` prints them. A check runs nothing and downloads nothing. Once a build succeeded, `StaleOutputs` deletes what the previous builds wrote and it did not.
- `Douze/Workflow/Steps/`: the built-in steps (`uses:` ids `data`, `frontmatter`, `parse`, `markdown`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `write`, `hover-typescript`, `hover-csharp`, `hover-go`...).
- `Douze/CodeHover/`: what the steps that check code fences with a language server share (`LspConnection`, `LspClient`, the twoslash result format); `CodeFenceStep` holds their memo and error reporting, `CodeFenceScanner` the fence scan they share with `check-code`.
- `Douze/Validation/`: the validators of the `check-code` step (`Workflow/Steps/CheckCodeStep.cs`, `docs/site-features-plan.md` 3.20): in-process parsers by language (`CodeValidators`: SharpYaml, System.Text.Json, `XmlReader`, the build's Fluid parser) and `WorkflowSnippet`, which checks a `douze.yml` sample with `WorkflowLoader` and `StepFactory`. `Douze/Twoslash/` is the TypeScript engine, `Douze/CodeHover/CSharp/` the C# one (Roslyn language server, generated projects, snippet wrapping). `Douze/Tools/` installs their servers. `docs/code-hover-steps.md` is the design.
- `Douze/Workflow/Cache/`: the disk cache (`<source>/.douze/cache/`, `--no-cache`, `--cache-dir`). `BuildCache` stores the
  result of every unit by `ResultKey` (step, configuration, input refs) with its dependency set, in pack files
  (`PackFile`) under a binary index (`CacheIndex`); `NodeSerializer` writes metadata and nodes keeping their types. The
  scheduler replays a unit whose key is cached and whose dependencies did not change (`Scheduling/ReplayState`). A watch
  session keeps the cache open: each build commits to it in memory (`Commit`), the session writes the index once
  (`Persist`).
- `Douze/Workflow/Watch/`: watch mode and the dev server. `FileWatcher` batches the changes of the source folder into a
  `ChangeSet`; `IncrementalBuilder` runs the builds of a session, keeping the cache, the template loader (invalidating
  changed files) and the source snapshot; `DevServer` serves the output folder (`HttpListener`) and `LiveReload` sends
  the changed URLs to `livereload.js` over a WebSocket (`/__douze/socket`; an event stream would hold one of the browser's 6 connections per host for each open page).
- `Douze/Workflow/Yaml/`: `douze.yml` loading. `YamlSource` keeps the position of every node, `WorkflowLoader` reads `site` (`SiteSettings`) and `steps` and reports every error with its line and column, `WorkflowSchema` is the field table shared by the loader and `--print-workflow`.
- `Douze/Workflow/Commands/`: `run:` steps in the modes `filter` (a process per file), `batch` (a process per input or group) and `worker` (one process, JSON lines over stdin and stdout, `WorkerProtocol`). `ProcessRunner` starts processes and kills their tree on timeout (`ProcessTree`, which avoids `Process.Kill(entireProcessTree: true)` on Unix: it can hang forever on macOS); `douze-worker.mjs` is the Node helper printed by `douze worker-helper`.
- `Douze/Twoslash/`: the `hover-typescript` step's engine (`Workflow/Steps/TwoslashStep.cs`): twoslash's notations
  (`TwoslashMarkup`, a port of twoslash's parsing, positions in UTF-16 code units like JavaScript), the words to hover
  (`IdentifierScanner`), and `TwoslashChecker`, which checks every snippet of a build with a TypeScript 7 language
  server (`tsc --lsp --stdio`, or tsrs, its Rust port: `engine`; `LspConnection`): one project and one language server per set of compiler options, hovers read back from the
  Markdown of the hover (`QuickInfo`), results in twoslash's shape (`TwoslashResult`). Results are memoized in the disk
  cache (`BuildCache.TryGetMemo`/`PutMemo`, kept per step).
- `Douze/Tools/`: the language servers of the code hover steps: native tools published on npm as per-platform
  packages (`NpmTool`: TypeScript 7, tsrs), NuGet packages (`NuGetTool`: the Roslyn language server) and Go modules
  (`GoTool`: gopls, built with `go install`), installed once into `<cache>/tools/<name>/<version>/[<platform>/]` by
  `ToolInstaller` (integrity verified, pinned for the default version, a lock file for concurrent builds). A step that
  downloads a tool declares a `ToolRequirement` (`NpmToolRequirement`, `NuGetToolRequirement`, `GoToolRequirement`),
  which `douze tools install` installs ahead. Pin a new default version with the integrity of every platform from the
  registry.
- `Douze/Highlighting/`: the `highlight` step's engine (`Workflow/Steps/HighlightStep.cs`). `TextMateBundle` reads the
  bundled grammars and themes (`textmate.zip`, written by `scripts/update-textmate.cs`: tm-grammars and tm-themes at the
  versions of Shiki); `CodeTokenizer` tokenizes with TextMateSharp (one pool of registries per language: a grammar is not
  used by two threads at once; grammars read into TextMateSharp's rules by Douze, whose reader drops numbers and booleans)
  and styles tokens in every theme at once;
  `CodeTheme` normalizes themes and resolves styles with vscode-textmate's rule trie, `Color` is the contrast arithmetic;
  `CodeMeta` parses fence metas, `CodeBlockBuilder` builds the block model (frames, markers, sections, twoslash nodes)
  that the Liquid templates render (`Templates/`, the defaults).
- `Douze/Markdown/`: Markdown rendering. `MarkdownOptions` is the `markdown:` section of `douze.yml`;
  `MarkdownPipelineFactory` builds the one Markdig pipeline of a build, which `MarkdownRenderer` (`BuildContext.Markdown`)
  renders with pooled renderers: anything that renders Markdown uses it, never a second configuration. Douze's own
  markup: raw regions (`RawRegionExtension`), asides (`AsideExtension`), code fences (`CodeFenceRenderer`), `.md`
  links (`LinkRewriter`). `HeadingScanner` and `Slugger` give the `headings` step the headings of rendered HTML.
- `Douze/Liquid/`: the Fluid bridge. `TemplateLoader` parses templates once per content hash and serves includes; `MetadataValue` is the model of every render.
- `Douze/Liquid/Filters/`: Douze's filters (`DataFilters`, `OutputFilters`, `LocaleFilters`, `NavigationFilters`), each with a static `Register` called by `LiquidOptions.Create`; `FilterHelpers` resolves names like a template does.
  `DouzeParser` (the parser of a build, from `LiquidOptions.CreateParser`) exposes Fluid's protected argument and
  expression parsers to Douze's tags in `Tags/`: shortcodes (`ShortcodeRegistry`, scanned by `BuildContext.CreateAsync`
  before any template is parsed, `ShortcodeTag`, `ShortcodeModel`), `markdown`, `include_markdown` and `slot`
  (`MarkdownTags`), `error` and `warn` (`DiagnosticTags`). `FenceTable` protects the fences of Markdown pages in the
  `liquid` step.
- `Douze/Images/`: the libvips bridge. `ImageProcessor` (one per build, `BuildContext.Images`, created on first use) probes, encodes variants (thumbnail, `keep=none`), rasterizes SVG and composites Pango text, with `Concurrency = 1` and no operation cache since the engine parallelizes per unit; `ImageFormats` reads SVG and GIF sizes without libvips; `ImageBuilder` names variants `<name>-<width>.<hash8>.<ext>`; `PictureMarkup` writes the `<picture>` of the `image` tag (`Liquid/Tags/ImageTag.cs`) and the `pictures` step.
- `Douze/Progress/`: the views of a build: `PlainProgressView` (a line per step), `SpectreProgressView` (live table and summary with a timeline), `SilentProgressView`; `StepSummary` formats what they share.
- `Douze/Progress/`: the views of a build: `PlainProgressView` (a line per step), `SpectreProgressView` (live table and summary with a timeline), `SilentProgressView`, `RebuildProgressView` (a line per rebuild of a watch session); `StepSummary` formats what they share.
- `Douze/Tracing/`: `TraceRecorder` records build, step, command and (with `--trace-units`) unit spans; `ChromeTraceWriter` writes them for Perfetto (`--trace`), `TimelineHtmlWriter` into the self-contained `timeline.html` (`--timeline`).
- `Douze.Tests.FakeTool/`: the fake external tool of the command step tests, launched with `dotnet Douze.Tests.FakeTool.dll <mode>`; it is in `Douze.slnx` so that it builds in the configuration of the tests.
- `bench/`: the benchmark harness (`Douze.Bench`: a synthetic site, the pages of a real-world API reference) and its results (`bench/README.md`). `bench/ssg-comparison/` compares Douze with other generators in Docker.
- `docs/`: the documentation site (<https://sebastienros.github.io/douze/>), built by Douze itself (`docs/douze.yml`). The PR checks (`pr.yml`) build it with the pull request's Douze on every platform, a functional test of Douze, and check it on Linux (lint, links, Playwright); `docs.yml` deploys it from `main`. Pages are in `docs/_content/douze/` (`tutorial/`, `guide/`, `recipes/`, `migrate/`, `reference/`), the navigation in `docs/_data/sidebar.yml`, the theme in `docs/_includes/` and `docs/_content/douze/assets/`; `docs/README.md` has the writing rules. The design documents (`docs/*-plan.md`, `docs/code-hover-steps.md`) are not part of the site.

## Conventions

- Keep the documentation site in sync with the code. A change that adds, alters or removes a feature (a step or one of
  its options, a command line option, a `douze.yml` key, a filter, a tag, a template variable, a default, a behavior
  or an error message that a page shows) updates the pages that describe it in the same change: the reference first
  (`docs/_content/douze/reference/`: `steps/<step>.md`, `cli.md`, `configuration.md`, `liquid.md`,
  `template-data.md`, `commands.md`), then the guide, recipe and tutorial pages that use it (search `docs/_content`
  for the name), and the README when it documents it. A new step or page gets an entry in `docs/_data/sidebar.yml`.
  Build the site with the changed Douze (`dotnet run --project Douze -- -s docs`) and fix what it reports; a change
  to the tutorial or the install page is checked with `docs/tests/docker/`.
- Docs, comments and names don't name the external projects Douze is evaluated against: describe them generically (the target site, a real-world API reference data set).
- Builds must be deterministic: files are processed in parallel, so anything collected across files must be sorted before it is exposed (reduce and group inputs, collections, results).
- `VirtualFile` is immutable: a step returns new files (`With`, `Derived`) or the same instance. Metadata nodes are shared between files and never mutated once published: shallow clone (`Metadata.With`) and replace.
- Data is never copied into files. Folder and site data live in scopes, and steps read metadata only through `ScopeSet.TryResolve`, which records what a unit depends on: a unit is replayed from the cache only while every key it resolved beyond a file's own metadata resolves to the same value (or, for templates, to the same part of it: `ValueAccess`). A step that cannot record what it reads must not be incremental. `NodeHash` caches the hash of objects and arrays once computed: never change a node after it is published.
- Data files are keyed by file name: `_data/colors.json` is `colors`. The nearest scope wins: own data, folder data (nearest folder first), site data, the `site:` settings of `douze.yml` (`site.url`, whose `site` object is completed rather than hidden by a `site` data object), then the front matter of layouts.
- Collection items (`collections.all`, `collections.<tag>`) are sorted by date ascending, using front matter `date` if defined, or `page.date` (the file date) otherwise, then by path (ordinal). This matches Eleventy.
- `collections.urls` keys pages by normalized url (`LocaleSet.NormalizeUrl`: no trailing `index.html`); the navigation and locale filters compare urls the same way.
- Filters read page and site data through the model (`FilterHelpers.ResolveAsync`, member by member), never from the scopes directly, so that a render depends on what it read; a filter that uses a whole value reads it whole (`NodeArrayValue.ReadAll`), and one that uses the locales reads `site.locales` (`FilterHelpers.GetLocalesAsync`). A step that looks values up from C# records its member reads on the `AccessRecorder` that `ScopeSet.TryResolveTracked` returns (`LocalizeStep`).
- A page and its locale fallback copies are variants (`VirtualFile.Variant`, set by `localize`): a map step that
  declares `RecordsOwnReads` has its units record every own metadata key they read (`ScopeSet.TryResolve` does it;
  C# reads of `Metadata.Own` call `DependencyTracker.RecordOwn`, and a result that depends on the path beyond output
  paths keeping its folders calls `RecordPath`), and the scheduler gives a copy the result of its page when the unit
  read none of the keys they differ by (`Scheduling/VariantSharing`). A step that cannot record what it reads (a
  command) keeps the default, and runs every copy.
- `permalink` removes the locale root (`site.locales.root`) from the paths under it, so that locale folders are url prefixes (`/ja/x/`).
- Pagination pages (`pagination.pages`, `page.next`, ...) only link pages generated from the same template.
- Tags reach the build through the ambient values of the render (`AmbientKeys`: the step context, the page's model,
  the tracker, whether the output is Markdown, the page's fence table), never through statics. A tag that renders
  Markdown uses `TagRendering.RenderMarkdownAsync` (the site's pipeline), restores the page's fences in a body before
  (`TagRendering.RenderBodyAsync` does), and records the files it reads (`DependencyTracker.RecordTemplate`, absent
  files included). In a Markdown page, block HTML from a tag is written as a raw region
  (`RawRegionExtension.Encode`: one line, so that it stays in the tag's list item).
- Repository scripts and helpers are C# file-based apps (`dotnet run scripts/<name>.cs`), not Python; their packages are versioned in `Directory.Packages.props`. Shell and PowerShell stay for the install scripts that users run (`install.sh`, `install.ps1`) and their test driver.
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

Build and serve the documentation site (`http://localhost:8080/douze/`), then check it like CI does:

```bash
dotnet run --project Douze -- serve -s docs
cd docs && npm ci && npm run lint && npm run check-links && npm run test:e2e
```

Compare the Sample output with another branch's Douze (lines with `Generated on` are ignored); any difference must be intentional and documented in the change:

```bash
scripts/compare-sample.sh <base-ref>
```

## Releases

- `tool-packages.yml` (a reusable workflow) builds what a version ships: the native AOT tool package of each platform (libvips included, no symbols), the CoreCLR fallback `any` (with the libvips of every platform under `runtimes/<rid>/native`), the pointer package, and a release archive per platform (`douze-<version>-<rid>.tar.gz`, `.zip` on Windows, `SHA256SUMS`). It tests `install.sh` and `install.ps1` against the packages on Linux, macOS and Windows (`scripts/test-install.ps1`, which serves them with `scripts/test-feed.cs`). PR checks run the same test with the package of their platform.
- Every push to `main` pushes these packages to Feedz with the version `<VersionPrefix>-preview.<run number>` (`build.yml`), then tests the install scripts against Feedz.
- Pushing a `v*` tag pushes them to nuget.org, then creates a GitHub Release with the archives and the install scripts (`publish.yml`). Publishing uses NuGet trusted publishing through the `release` environment, so there is no NuGet API key secret. Running `publish.yml` by hand (`gh workflow run publish.yml --ref <branch>`) is a dry run: it builds and tests everything and publishes nothing.
- `install.sh` and `install.ps1` are minimal NuGet clients: they read the flat container of the feed (nuget.org, Feedz for previews) for `douze.<rid>` and extract its tool folder into `<install dir>/<version>`, linked from `<install dir>/bin`.
