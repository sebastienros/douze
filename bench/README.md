# Benchmarks

`bench/Douze.Bench` generates large sites and measures the published native AOT `douze` building them
(`docs/workflow-engine-plan.md`, section 9). It is not packed and not part of `dotnet test`. Generated sites go under
`bench/*/data/` and outputs under `bench/*/out/`, both gitignored.

## Commands

Everything at once (publishes `tool-out/douze`, generates every scenario, prints the results table, median of 3 runs):

```bash
bench/run-all.sh
```

One step at a time:

```bash
dotnet publish Douze -c Release -o tool-out
dotnet build bench/Douze.Bench -c Release -o bench/out/bin

# Synthetic site of section 9.1: N pages of about 4 KB (title, date, 2 tags out of 20), layouts post -> base,
# includes header, nav (50 items of collections.all) and footer, 5 data files, a page paginated over 2,000 items
# (size 20) and 200 assets. --nav index loops over collections.all[0..49] instead of 'limit: 50', --nav none drops it.
bench/out/bin/Douze.Bench synthetic --pages 32000 --out bench/synthetic/data/32000

# aspire.dev API reference data (pkgs/*.json and ts-modules/*.json of origin/main, read from git objects: the
# checkout's branch and working tree are left alone), then a site rendering its API pages.
bench/fetch-aspire-data.sh ~/github/aspire.dev
bench/out/bin/Douze.Bench aspire-api --data bench/aspire/data --out bench/aspire/data/site
bench/out/bin/Douze.Bench aspire-api --data bench/aspire/data --out bench/aspire/data/site-data --mode data

# Clean builds (the output folder is deleted before each run); prints a Markdown table row with the median.
# Options after '--' go to douze.
bench/out/bin/Douze.Bench run --site bench/synthetic/data/32000 --out bench/out/site --repeat 3 --label "synthetic 32000"
bench/out/bin/Douze.Bench run --site bench/synthetic/data/32000 --out bench/out/site -- -p 4
```

`run` measures with `/usr/bin/time -l` on macOS, `/usr/bin/time -v` on Linux and `Process.PeakWorkingSet64`
(sampled) on Windows. Peak RSS is the douze process only.

## Scenarios

| Scenario | What it is |
|---|---|
| `synthetic N` | The site of section 9.1 through the default workflow |
| `synthetic 32000, nav index` | The same with a nav include that indexes `collections.all[i]` instead of `for ... limit: 50` |
| `synthetic 32000, nav none` | The same without the nav include |
| `aspire API data only` | The 342 JSON files of aspire.dev (78 MB) as site data and a single page |
| `aspire API pages` | aspire.dev's API routes rendered from that data: C# package (202), type (1,810) and member kind (2,829) pages, TypeScript module (140), item (4,097) and method (10,634) pages, 19,712 in all, each with a sidebar of the 342 packages |

The aspire site builds with the default workflow, since workflow files land in phase 2: the JSON files are site data
(`pkgs.<slug>`, `tsmodules.<slug>`), each package folder has its page index as folder data (`pages.json`), and one
template per route paginates over it with a size of 1. Section 4.7's workflow (`parse`, `liquid` with `group-by`)
remains to be added as a second variant once phase 2 is merged. The real site also has Markdown twins of some routes
(`*.md.ts`), which are not generated here.

## Results

### Phase 1b engine (commit 50e0be9), 2026-10-21

Apple M4 Pro, 14 cores, 48 GB, macOS 15.8.1 (APFS), native AOT `douze`, warm file cache, clean output folder, median of
3 runs. aspire.dev data from `origin/main` c26b59ba (202 packages, 140 TypeScript modules, 78.5 MB).

| Label | HTML pages | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) | Output files | Runs |
|---|---:|---:|---:|---:|---:|---:|---:|
| synthetic 1000 | 1101 | 0.43 | 0.40 | 0.65 | 165 | 1301 | 3 |
| synthetic 4000 | 4101 | 1.10 | 1.56 | 2.08 | 476 | 4301 | 3 |
| synthetic 8000 | 8101 | 2.60 | 4.56 | 3.61 | 842 | 8301 | 3 |
| synthetic 16000 | 16101 | 13.24 | 20.39 | 10.52 | 2618 | 16301 | 3 |
| synthetic 32000 | 32101 | 39.47 | 74.00 | 31.62 | 5976 | 32301 | 3 |
| synthetic 32000, nav index | 32101 | 6.87 | 6.89 | 22.58 | 3416 | 32301 | 3 |
| synthetic 32000, nav none | 32101 | 6.38 | 5.47 | 22.55 | 2911 | 32301 | 3 |
| synthetic 32000, nav index, -p 4 | 32101 | 6.86 | 9.87 | 10.08 | 3400 | 32301 | 3 |
| aspire API data only | 1 | 0.90 | 1.26 | 0.51 | 714 | 1 | 3 |
| aspire API pages | 19712 | 23.07 | 35.22 | 12.36 | 5473 | 19712 | 3 |

Against the targets of section 9.2:

| Target | Result | |
|---|---|---|
| Synthetic 32k pages, clean build: wall under 30 s | 39.5 s; 6.9 s without the `limit:` loop (finding 1) | missed, met once finding 1 is fixed |
| Synthetic 32k pages, clean build: peak RSS under 1.5 GB | 6.0 GB; 3.4 GB without the `limit:` loop | missed (findings 1 and 3) |
| aspire data: parse 78 MB under 4 s, RSS under 1.2 GB | 0.9 s, 714 MB | met: the `JsonElement` backed `Node` of open question 14 is not needed yet |
| aspire data: render the API pages, wall under 60 s | 23.1 s for 19,712 pages, 16 s of which hash metadata on one thread (finding 2) | met |
| aspire data: render the API pages, peak RSS under 2.5 GB | 5.5 GB (4.5 to 5.9) | missed (findings 2 and 3) |
| Second build, watch mode, tag edit, worker round trip | | phases 3, 5 and 6 |

### Integration of phases 2, 3, 4 and B, 2026-10-21

Same machine, clean builds, `run` against the merge of the four phases (11793f6) and the integrated branch, whose
default workflow builds collections before pagination and runs Liquid before Markdown:

| Label | HTML pages | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) | Output files | Runs |
|---|---:|---:|---:|---:|---:|---:|---:|
| synthetic 32000, 11793f6 | 32101 | 39.12 | 73.21 | 31.84 | 6056 | 32301 | 3 |
| synthetic 32000, integrated | 32101 | 38.41 | 73.40 | 37.01 | 5621 | 32301 | 3 |
| aspire API pages, 11793f6 | 19712 | 23.23 | 38.50 | 17.49 | 5142 | 19712 | 1 |
| aspire API pages, integrated | 19712 | 10.42 | 25.86 | 15.13 | 5218 | 19712 | 1 |

The aspire pages are faster because a paginated template is now in `collections.all` once instead of once per page
(19,712 pages were collected, sorted and hashed with the shared pagination data of finding 2). Findings 1 to 3 are
otherwise unchanged.

### .NET 11 and runtime async, 2026-10-21

Same machine and data. Variants built from the same sources: **A** .NET 10 (the base commit 4eb91a5, SDK 10.0.400,
runtime 10.0.11), **B** .NET 11 RC 2 (SDK 11.0.100-rc.2.26504.105), **C** B with runtime async
(`<Features>runtime-async=on</Features>`). Each was published native AOT (`tool-out`) and framework dependent (JIT,
`-p:PublishAot=false`). Runs were interleaved (one run of every variant per round, 5 rounds with the default GC, 3 with
`DOTNET_gcConcurrent=0`); the table has the medians. AOT binary sizes (osx-arm64): A 16.12 MB, B 15.86 MB, C 15.64 MB.

| Scenario | Variant | GC | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) |
|---|---|---|---:|---:|---:|---:|
| synthetic 32000 | A AOT | default | 42.85 | 74.28 | 29.79 | 5635 |
| synthetic 32000 | B AOT | default | 59.65 | 95.95 | 23.15 | 6998 |
| synthetic 32000 | C AOT | default | 58.11 | 96.21 | 22.67 | 7078 |
| synthetic 32000 | A JIT | default | 49.09 | 88.36 | 25.47 | 8267 |
| synthetic 32000 | B JIT | default | 70.29 | 116.83 | 23.91 | 6933 |
| synthetic 32000 | C JIT | default | 68.58 | 115.56 | 24.04 | 6952 |
| synthetic 32000 | A AOT | non concurrent | 45.39 | 71.81 | 23.77 | 5807 |
| synthetic 32000 | B AOT | non concurrent | 33.02 | 63.08 | 16.89 | 6243 |
| synthetic 32000 | C AOT | non concurrent | 33.62 | 65.19 | 16.64 | 6312 |
| synthetic 32000 | A JIT | non concurrent | 45.62 | 79.27 | 25.54 | 6085 |
| synthetic 32000 | B JIT | non concurrent | 40.81 | 86.77 | 18.07 | 6545 |
| synthetic 32000 | C JIT | non concurrent | 40.97 | 86.17 | 18.05 | 6111 |
| aspire API pages | A AOT | default | 8.97 | 20.82 | 10.72 | 5566 |
| aspire API pages | B AOT | default | 7.85 | 26.96 | 11.94 | 9502 |
| aspire API pages | C AOT | default | 6.80 | 23.77 | 11.05 | 11231 |
| aspire API pages | A JIT | default | 8.03 | 32.58 | 9.19 | 11347 |
| aspire API pages | B JIT | default | 9.72 | 46.99 | 11.66 | 11042 |
| aspire API pages | C JIT | default | 8.35 | 38.97 | 10.26 | 11701 |
| aspire API pages | A AOT | non concurrent | 7.64 | 19.18 | 9.34 | 5544 |
| aspire API pages | B AOT | non concurrent | 7.04 | 19.88 | 9.57 | 4919 |
| aspire API pages | C AOT | non concurrent | 6.51 | 18.94 | 9.30 | 6391 |
| aspire API pages | A JIT | non concurrent | 8.93 | 30.14 | 8.34 | 4897 |
| aspire API pages | B JIT | non concurrent | 8.80 | 41.51 | 7.28 | 5739 |
| aspire API pages | C JIT | non concurrent | 8.60 | 40.46 | 7.04 | 5705 |

Peak RSS of the aspire pages varies a lot between runs (4.3 to 7.1 GB for A AOT, 4.8 to 6.7 GB for C AOT non
concurrent); the synthetic numbers are stable within about 5%. Single runs of `synthetic 32000, nav index` (no `limit:`
loop): A AOT 6.8 to 7.5 s and 3.4 GB; B and C AOT with the default GC 6.4 to 7.3 s and 2.9 to 3.0 GB; C AOT non
concurrent 5.6 to 5.7 s and 3.4 GB.

What changed:

- With the default (concurrent) workstation GC, .NET 11 is 40% slower on the synthetic site, all of it in `layout`
  (38 s against 58 s), the step where the nav include runs the `limit:` loop of finding 1, and the aspire pages need
  up to twice the memory. A `sample` of the .NET 11 build shows the allocating threads sleeping in
  `gc_heap::allocate_soh` (`GCToOSInterface::Sleep`) while a background GC runs: allocations are throttled during
  background GCs, which the allocation rate of finding 1 triggers back to back. Server GC (`DOTNET_gcServer=1`) does
  not help (57.6 s).
- With concurrent GC off, .NET 11 is faster than .NET 10 in every configuration: synthetic 33.0 s against 42.9 s (A
  with its default GC; A gets slower without concurrent GC), aspire 6.5 to 7.0 s against 9.0 s, and 5.7 s against 7.0 s
  without the `limit:` loop. Peak RSS is 10% higher on the synthetic site (6.2 GB against 5.6 GB, garbage of finding 1
  collected less eagerly) and lower or equal on the aspire pages.
- Runtime async changes nothing measurable: B and C are within noise of each other everywhere, and the BCL is
  compiled with it in both. Douze's own async methods lose their state machines (167 compiler generated state machine
  types in `douze.dll` against 11 left, the async iterators), and the AOT binary is 216 KB smaller.

Douze moved to .NET 11 with runtime async and `ConcurrentGarbageCollection=false` (`Douze/Douze.csproj`). The
published binary built that way: synthetic 32000 34.1 s and 6.5 GB (single run), nav index 5.6 to 5.7 s and 3.4 GB.

## Findings

Measured on the engine of phase 1b. None of these are fixed here (phase B owns only `bench/`); they feed phase 5.

1. **`{% for p in collections.all limit: 50 %}` costs O(N) per page, O(N²) per build.** The nav include alone takes
   the synthetic 32k build from 6.9 s to 39.5 s and from 3.4 GB to 6.0 GB; with `collections.all[i]` for i in 0..49
   the same output costs almost nothing (6.9 s against 6.4 s without a nav). Fluid's `for` enumerates the whole
   `NodeArrayValue` (and wraps every item in a `MetadataValue`) before applying `limit` and `offset`. Suggested fix:
   apply `offset` and `limit` while enumerating in Fluid's `ForStatement` (Fluid 3 is still in preview), or let
   `NodeArrayValue` expose a sliced enumeration that the `for` tag uses; either way, wrap items lazily. Eleventy sites
   loop over collections with `limit` all the time, so this is the first thing to fix.

2. **Metadata hashing of paginated pages is O(pages²) per paginated file, and the reduce barrier does it on one
   thread.** `Metadata.Hash` serializes the whole `Own` of a page, which holds `pagination.pages` and
   `pagination.hrefs`, shared by every page of the file. The aspire site has files with up to 2,592 pages
   (Σ pages² = 12.7 million summaries per hash pass); `collections` spends 16 of the build's 23 s in
   `RunReduceAsync` computing `files.Select(x => x.Ref)` with one thread busy in `CanonicalJson.Write` and 13 idle,
   and `permalink` takes another few seconds rehashing the same pages. Suggested fixes: cache a hash on each published
   `NodeObject` / `NodeArray` (they are immutable once published, as `NodeValue<Metadata>` already is through
   `$metadata`) so a shared subtree is hashed once; compute the refs of a reduce or group input in parallel; and
   consider leaving `pages`/`hrefs` out of the per-page hash (they are a function of the source file and its data,
   which are already in the unit's key).

3. **Every intermediate content stays in memory until the build ends (open question 18 matters).** A heap dump of the
   32k synthetic build (`--nav index`, JIT build, during `write`) holds 2.8 GB in 4.1 million objects, mostly 128,000
   strings over 10 KB: four UTF-16 copies of each page (front matter, Markdown, render and layout outputs) kept by
   `StepRun.Results` (each `UnitResult` keeps its output files), plus about 32,000 parsed page templates kept by the
   template cache (`GetOrParse` by content hash, never reused for pages) and the `WriteStep._written` map. The live
   set grows by about 100 KB per page, so peak RSS is linear but 2 to 4 times the 1.5 GB target at 32k pages.
   Suggested fixes for phase 5: in a one-shot build, keep only refs and dependency sets in `UnitResult` (the disk
   cache, not memory, is what a later build replays), release a file's content once `write` has written it, keep page
   content as UTF-8 (`TextContent` holds UTF-16 strings), and do not cache templates parsed from page content (or
   evict them when the unit ends). Watch mode (phase 6) can then choose what to keep.

4. **Writing is bound by file system syscalls and does not scale past about 4 threads.** After the collections
   barrier, `mkdir` and `open` dominate the samples (APFS serializes directory updates in a parent folder: 500 page
   folders per section here). The 32k build has 22.6 s of system time at `-p 14` against 10.1 s at `-p 4` for the same
   wall time (6.9 s). `PhysicalFileSystem` also checks and creates the directory twice per file
   (`WriteStep` calls `CreateDirectoryAsync`, then `CreateFileAsync` does it again) and opens files with
   `FileOptions.Asynchronous`, which is thread pool emulation on Unix. Suggested fixes: one `Directory.CreateDirectory`
   per distinct folder (a concurrent set of created folders), synchronous `File.Create` / `RandomAccess.Write`, and a
   write concurrency limit of its own (about 4) so CPU slots stay with rendering.
   *Integration:* `WriteStep` no longer creates the directory itself (`CreateFileAsync` does); the other
   suggestions are left for phase 5.

5. **A build can hang after an exception outside a unit.** Under `DOTNET_GCHeapHardLimit=20000000` (512 MB) the
   16k synthetic build (`--nav index`) either fails cleanly (`error: pages: Insufficient memory...`, seen with
   `--plain`) or hangs (2 of 3 runs, both with `-q`) at 0% CPU with the main thread in `Task.Wait` and every worker idle. The likely cause is an exception thrown
   in `NodeRunner.CompleteAsync` or in the reporting of a failure, which skips `Output.Complete` and
   `Completed.TrySetResult`, so dependents wait on a channel that never completes. Suggested fix: complete the output
   channel and `Completed` in `finally` blocks that cannot be skipped, and add a test that throws from a view callback.
   *Integration:* fixed that way (`SchedulerTests.ExceptionOutsideAUnitFailsTheBuild`); such an exception now fails
   the step and cancels the build.

6. **Smaller observations.** Loading the aspire data is a single unit of the `data` step: 0.9 s wall for 1.26 s of
   CPU, so parsing the 342 files in parallel would make it about 0.3 s. RSS for the data is about 9 times the JSON size
   (714 MB for 78.5 MB), within the target. The render and layout steps parse each page's template once, which is
   cheap next to findings 1 to 3.

7. **The GC mode matters as much as the code on .NET 11.** Background GCs throttle allocating threads, which made the
   allocation heavy `limit:` loop 40% slower than on .NET 10 until `douze` turned concurrent GC off (".NET 11 and
   runtime async" above). Re-measure the GC modes (concurrent, non concurrent, Server GC with DATAS) after each memory
   change of phase 5: once findings 1 and 3 cut the allocation rate and the live set, concurrent GC or a lower peak RSS
   from a heap limit (`GCHeapHardLimit`, `GCConserveMemory`) may become the better trade.

Suggested order for phase 5: finding 2 (hash caching, it also makes cache keys cheap), then 3 (results and content
retention, which the disk cache design decides anyway), then 4 and 5. Finding 1 is a Fluid change and can land at any
time.
