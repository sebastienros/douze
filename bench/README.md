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

# A real-world API reference data set: the JSON folders of a documentation repository (per-package .NET API files in
# pkgs, per-module TypeScript API files in ts-modules) at origin/main, read from git objects (the checkout's branch and
# working tree are left alone), then a site rendering its API pages.
bench/fetch-api-data.sh <checkout> bench/api-reference/data <path>/pkgs <path>/ts-modules
bench/out/bin/Douze.Bench api-reference --data bench/api-reference/data --out bench/api-reference/data/site
bench/out/bin/Douze.Bench api-reference --data bench/api-reference/data --out bench/api-reference/data/site-data --mode data

# Clean builds (the output folder is deleted before each run); prints a Markdown table row with the median.
# Options after '--' go to douze.
bench/out/bin/Douze.Bench run --site bench/synthetic/data/32000 --out bench/out/site --repeat 3 --label "synthetic 32000"
bench/out/bin/Douze.Bench run --site bench/synthetic/data/32000 --out bench/out/site -- -p 4

# Builds from the disk cache (phase 5): --cache clean (default: no output folder, no cache), warm (a second build
# without changes), edit (a paragraph appended to the middle page before each build), tag (its tags changed).
bench/out/bin/Douze.Bench run --site bench/synthetic/data/32000 --out bench/out/site --cache edit
```

`run` measures with `/usr/bin/time -l` on macOS, `/usr/bin/time -v` on Linux and `Process.PeakWorkingSet64`
(sampled) on Windows. Peak RSS is the douze process only.

## Scenarios

| Scenario | What it is |
|---|---|
| `synthetic N` | The site of section 9.1 through the default workflow |
| `synthetic 32000, nav index` | The same with a nav include that indexes `collections.all[i]` instead of `for ... limit: 50` |
| `synthetic 32000, nav none` | The same without the nav include |
| `API reference data only` | The 342 JSON files of the API reference (78 MB) as site data and a single page |
| `API reference pages` | The API routes of its documentation site rendered from that data: C# package (202), type (1,810) and member kind (2,829) pages, TypeScript module (140), item (4,097) and method (10,634) pages, 19,712 in all, each with a sidebar of the 342 packages |

The API reference site builds with the default workflow, since workflow files land in phase 2: the JSON files are site data
(`pkgs.<slug>`, `tsmodules.<slug>`), each package folder has its page index as folder data (`pages.json`), and one
template per route paginates over it with a size of 1. Section 4.7's workflow (`parse`, `liquid` with `group-by`)
remains to be added as a second variant once phase 2 is merged. The real site also has Markdown twins of some routes
(`*.md.ts`), which are not generated here.

## Results

### Phase 1b engine (commit 50e0be9), 2026-10-21

Apple M4 Pro, 14 cores, 48 GB, macOS 15.8.1 (APFS), native AOT `douze`, warm file cache, clean output folder, median of
3 runs. API reference data from `origin/main` c26b59ba (202 packages, 140 TypeScript modules, 78.5 MB).

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
| API reference data only | 1 | 0.90 | 1.26 | 0.51 | 714 | 1 | 3 |
| API reference pages | 19712 | 23.07 | 35.22 | 12.36 | 5473 | 19712 | 3 |

Against the targets of section 9.2:

| Target | Result | |
|---|---|---|
| Synthetic 32k pages, clean build: wall under 30 s | 39.5 s; 6.9 s without the `limit:` loop (finding 1) | missed, met once finding 1 is fixed |
| Synthetic 32k pages, clean build: peak RSS under 1.5 GB | 6.0 GB; 3.4 GB without the `limit:` loop | missed (findings 1 and 3) |
| API reference data: parse 78 MB under 4 s, RSS under 1.2 GB | 0.9 s, 714 MB | met: the `JsonElement` backed `Node` of open question 14 is not needed yet |
| API reference data: render the API pages, wall under 60 s | 23.1 s for 19,712 pages, 16 s of which hash metadata on one thread (finding 2) | met |
| API reference data: render the API pages, peak RSS under 2.5 GB | 5.5 GB (4.5 to 5.9) | missed (findings 2 and 3) |
| Second build, watch mode, tag edit, worker round trip | | phases 3, 5 and 6 |

### Integration of phases 2, 3, 4 and B, 2026-10-21

Same machine, clean builds, `run` against the merge of the four phases (11793f6) and the integrated branch, whose
default workflow builds collections before pagination and runs Liquid before Markdown:

| Label | HTML pages | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) | Output files | Runs |
|---|---:|---:|---:|---:|---:|---:|---:|
| synthetic 32000, 11793f6 | 32101 | 39.12 | 73.21 | 31.84 | 6056 | 32301 | 3 |
| synthetic 32000, integrated | 32101 | 38.41 | 73.40 | 37.01 | 5621 | 32301 | 3 |
| API reference pages, 11793f6 | 19712 | 23.23 | 38.50 | 17.49 | 5142 | 19712 | 1 |
| API reference pages, integrated | 19712 | 10.42 | 25.86 | 15.13 | 5218 | 19712 | 1 |

The API reference pages are faster because a paginated template is now in `collections.all` once instead of once per page
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
| API reference pages | A AOT | default | 8.97 | 20.82 | 10.72 | 5566 |
| API reference pages | B AOT | default | 7.85 | 26.96 | 11.94 | 9502 |
| API reference pages | C AOT | default | 6.80 | 23.77 | 11.05 | 11231 |
| API reference pages | A JIT | default | 8.03 | 32.58 | 9.19 | 11347 |
| API reference pages | B JIT | default | 9.72 | 46.99 | 11.66 | 11042 |
| API reference pages | C JIT | default | 8.35 | 38.97 | 10.26 | 11701 |
| API reference pages | A AOT | non concurrent | 7.64 | 19.18 | 9.34 | 5544 |
| API reference pages | B AOT | non concurrent | 7.04 | 19.88 | 9.57 | 4919 |
| API reference pages | C AOT | non concurrent | 6.51 | 18.94 | 9.30 | 6391 |
| API reference pages | A JIT | non concurrent | 8.93 | 30.14 | 8.34 | 4897 |
| API reference pages | B JIT | non concurrent | 8.80 | 41.51 | 7.28 | 5739 |
| API reference pages | C JIT | non concurrent | 8.60 | 40.46 | 7.04 | 5705 |

Peak RSS of the API reference pages varies a lot between runs (4.3 to 7.1 GB for A AOT, 4.8 to 6.7 GB for C AOT non
concurrent); the synthetic numbers are stable within about 5%. Single runs of `synthetic 32000, nav index` (no `limit:`
loop): A AOT 6.8 to 7.5 s and 3.4 GB; B and C AOT with the default GC 6.4 to 7.3 s and 2.9 to 3.0 GB; C AOT non
concurrent 5.6 to 5.7 s and 3.4 GB.

What changed:

- With the default (concurrent) workstation GC, .NET 11 is 40% slower on the synthetic site, all of it in `layout`
  (38 s against 58 s), the step where the nav include runs the `limit:` loop of finding 1, and the API reference pages need
  up to twice the memory. A `sample` of the .NET 11 build shows the allocating threads sleeping in
  `gc_heap::allocate_soh` (`GCToOSInterface::Sleep`) while a background GC runs: allocations are throttled during
  background GCs, which the allocation rate of finding 1 triggers back to back. Server GC (`DOTNET_gcServer=1`) does
  not help (57.6 s).
- With concurrent GC off, .NET 11 is faster than .NET 10 in every configuration: synthetic 33.0 s against 42.9 s (A
  with its default GC; A gets slower without concurrent GC), API reference 6.5 to 7.0 s against 9.0 s, and 5.7 s against 7.0 s
  without the `limit:` loop. Peak RSS is 10% higher on the synthetic site (6.2 GB against 5.6 GB, garbage of finding 1
  collected less eagerly) and lower or equal on the API reference pages.
- Runtime async changes nothing measurable: B and C are within noise of each other everywhere, and the BCL is
  compiled with it in both. Douze's own async methods lose their state machines (167 compiler generated state machine
  types in `douze.dll` against 11 left, the async iterators), and the AOT binary is 216 KB smaller.

Douze moved to .NET 11 with runtime async and `ConcurrentGarbageCollection=false` (`Douze/Douze.csproj`). The
published binary built that way: synthetic 32000 34.1 s and 6.5 GB (single run), nav index 5.6 to 5.7 s and 3.4 GB.

### Fluid 3.0.0-preview-780 (`IFluidArray`), 2026-10-21

Same machine and data, native AOT, non concurrent GC. **Base** is 00f309e (Fluid preview-776), **head** reads
`NodeArrayValue` items by index through Fluid's new `IFluidArray` (sebastienros/fluid#1057): `for ... limit:` and the
`first`, `last` and `slice` filters convert only the items they use. Runs interleaved, median of 3:

| Scenario | Variant | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) |
|---|---|---:|---:|---:|---:|
| synthetic 32000 | base | 34.34 | 63.81 | 17.37 | 6172 |
| synthetic 32000 | head | 6.08 | 5.60 | 19.67 | 3207 |
| synthetic 32000, nav index | base | 6.32 | 6.33 | 19.81 | 3212 |
| synthetic 32000, nav index | head | 6.31 | 6.38 | 19.74 | 3247 |
| API reference pages | base | 7.13 | 19.48 | 9.13 | 5487 |
| API reference pages | head | 6.95 | 18.93 | 9.12 | 5019 |

Finding 1 is fixed: the `limit:` loop now costs what the indexed loop costs (6.1 s against 6.3 s), the synthetic site
builds in 6.1 s instead of 34.3 s and its peak RSS drops from 6.2 to 3.2 GB. The API reference pages do not use `limit:` and
are unchanged (their RSS varies between 4.8 and 5.9 GB from run to run, as before).

### Phase 5: disk cache, hashing and memory, 2026-10-21

Same machine and data, native AOT, Fluid 3.0.0-preview-781, non concurrent GC, median of 3 runs. *Clean* builds start
without an output folder and without a cache (`.douze/`), and either do not use the cache (`--no-cache`) or write it.
*No change*, *one page edited* and *one tag edited* start from the output and cache of a previous build of the same site
(`run --cache warm|edit|tag`; the edit appends a paragraph to the middle page, so its date changes too).

| Scenario | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) |
|---|---:|---:|---:|---:|
| synthetic 32000, clean, no cache | 5.51 | 7.55 | 18.57 | 2318 |
| synthetic 32000, clean, writing the cache | 6.78 | 9.14 | 18.76 | 2665 |
| synthetic 32000, no change | 1.14 | 2.97 | 0.71 | 449 |
| synthetic 32000, one page edited | 1.73 | 3.26 | 1.31 | 752 |
| synthetic 32000, one tag edited | 1.50 | 3.30 | 0.77 | 747 |
| API reference pages, clean, no cache | 3.52 | 5.55 | 11.28 | 2440 |
| API reference pages, clean, writing the cache | 4.08 | 6.08 | 10.82 | 2625 |
| API reference pages, no change | 1.07 | 1.80 | 0.47 | 704 |

The cache takes 794 MB for the synthetic site and 964 MB for the API reference pages (contents of every step, metadata, data).
Against the base of the phase (00f309e: synthetic 34.3 s and 6.2 GB, API reference 7.1 s and 5.5 GB): finding 1 (Fluid
`IFluidArray`) took the synthetic site to 6.1 s and 3.2 GB; finding 2 (Merkle hashes cached on nodes, parallel refs) took
the API reference pages from 7.0 to 3.4 s and 5.0 to 3.3 GB; finding 3 (refs instead of contents in results, sources released,
weak page templates, no UTF-8 copy kept by `write`) took the synthetic site to 2.3 GB and the API reference pages to 2.4 GB.
Precise dependencies cost about 2 s of user CPU on the synthetic site (8 s against 5.7 s, spread over 14 cores; the wall
time did not change): a page records what its templates read of `collections`.

Against the targets of section 9.2:

| Target | Result | |
|---|---|---|
| Synthetic 32k, clean build: wall under 30 s | 5.5 s (6.8 s writing the cache) | met |
| Synthetic 32k, clean build: peak RSS under 1.5 GB | 2.3 GB (2.7 GB writing the cache) | missed, see below |
| Synthetic 32k, second build, no change: under 3 s | 1.1 s, 0 units executed | met |
| Synthetic 32k, one page edited: under 2 s from the disk cache (phase 5 acceptance) | 1.7 s, 8 units executed | met |
| Synthetic 32k, one tag edited: under 5 s | 1.5 s | met |
| API reference data: render the API pages, wall under 60 s | 3.5 s | met |
| API reference data: render the API pages, peak RSS under 2.5 GB | 2.4 GB (2.6 GB writing the cache) | met without writing the cache |

Where the synthetic site's memory goes: sampling RSS during a clean build shows about 0.8 GB at the collections barrier
(every page held, by design of the barrier), then a climb to 2.3 GB while `render`, `markdown` and `layout` run ahead of
`write`, which ends 1.5 s after `layout`: pages wait in the unbounded channels in front of the slowest step. Under
`DOTNET_GCHeapHardLimit=0x60000000` (1.5 GB) the build runs out of memory, so this is the live set, not GC slack, and
the GC modes do not change it (below). The fix is backpressure on the channels whose consumer reads as soon as its
producer writes (not those in front of a barrier, which must hold everything), with two conditions: a unit must not
hold its work slot while it waits to write its outputs (the consumer needs a slot), and a consumer that stops early must
release its producers. Left for a follow-up. *The follow-up ("Bounded channels and duplicate path tracking" above) found
that most of the climb was the files every node kept to detect duplicate paths, not the channels.*

GC modes, measured after findings 1 to 3 (clean builds without the cache, 2 runs each, wall and peak RSS):

| GC | synthetic 32000 | API reference pages |
|---|---|---|
| non concurrent (default) | 5.6 s, 2.6-2.8 GB | 3.6 s, 3.3 GB |
| `GCgen0size` 32 MB / 64 MB | 5.7-5.8 s, 2.6-2.7 GB | 3.5-3.6 s, 3.3 GB |
| `GCConserveMemory=5` | 6.0 s, 2.6-2.7 GB | 3.9 s, 3.3 GB |
| Server GC with DATAS | 5.7-5.9 s, 2.6 GB | 3.4 s, 3.3 GB |
| concurrent | 6.7-7.2 s, 2.5 GB | 4.4 s, 3.4 GB |

The non concurrent workstation GC stays the default: Server GC with DATAS (set with `DOTNET_gcServer=1`, which the
native AOT binary ignores: finding 8) ties with it, the other settings are slower
and none lowers the peak by more than 10%, since the peak is the live set. Most of the system time of a clean build
(about 18 s) is the kernel providing fresh memory to the GC and creating the output folders, not the GC itself.

### Bounded channels and duplicate path tracking, 2026-10-21

Same machine and data, native AOT, non concurrent GC. **Base** is 4b32b5e (phase 5 reviewed), **head** this change:
channels in front of map steps bounded to 2 files per slot (plan 2.6, risk 6), and nodes keep the refs of the files
they check for duplicate paths instead of the files. **Unbounded** is head with every channel unbounded, to separate the
two. Runs interleaved (one run of each variant per round), median of 3. This day's builds spend more system time than
the phase 5 table's (35 to 45 s against 18 s for the same clean build of the base), so compare within this table.

| Scenario | Variant | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) |
|---|---|---:|---:|---:|---:|
| synthetic 32000, clean, no cache | base | 6.53 | 8.46 | 39.54 | 2422 |
| synthetic 32000, clean, no cache | head | 6.26 | 9.21 | 39.96 | 1075 |
| synthetic 32000, clean, no cache | unbounded | 7.36 | 7.52 | 52.43 | 1109 |
| synthetic 32000, clean, writing the cache | base | 7.59 | 10.32 | 36.23 | 2679 |
| synthetic 32000, clean, writing the cache | head | 7.21 | 10.95 | 31.21 | 1221 |
| synthetic 32000, one page edited | base | 1.70 | 3.24 | 0.74 | 635 |
| synthetic 32000, one page edited | head | 1.56 | 4.64 | 0.85 | 578 |
| API reference pages, clean, no cache | base | 4.31 | 6.19 | 20.78 | 2428 |
| API reference pages, clean, no cache | head | 3.96 | 6.42 | 15.03 | 1113 |
| API reference pages, clean, no cache | unbounded | 4.42 | 5.77 | 26.96 | 1740 |
| API reference pages, clean, writing the cache | base | 4.98 | 6.60 | 19.65 | 2620 |
| API reference pages, clean, writing the cache | head | 4.74 | 7.25 | 16.36 | 1411 |

What phase 5 attributed to the channels was mostly elsewhere: every node kept every file that entered it, in the map it
uses to detect a path delivered by two producers (`NodeRunner._producers`), so every content of every step stayed in
memory until the build ended (`write` alone held the HTML of every page; found with `dotnet-gcdump` on a JIT build). With
refs kept instead, the synthetic site peaks at the collections barrier (about 1.05 GB: every page waits there, by design)
and no longer climbs while the pages stream to `write`. Bounded channels then matter for the API reference pages (1.1 GB against
1.7 GB unbounded) and for time: with them, `render`, `markdown` and `layout` no longer run ahead of `write` and compete with
it for the file system, and the synthetic build is 15% faster than unbounded (7.4 s to 6.3 s, 12 s less system time). The
one-page edit uses about 1.4 s more user CPU (the nodes with two producers hash on entry, in parallel) for a faster wall
time.

Against the targets of section 9.2:

| Target | Result | |
|---|---|---|
| Synthetic 32k, clean build: wall under 30 s | 6.3 s (7.2 s writing the cache) | met |
| Synthetic 32k, clean build: peak RSS under 1.5 GB | 1.08 GB (1.22 GB writing the cache) | met (2.3 GB before) |
| Synthetic 32k, one page edited: under 2 s from the disk cache | 1.6 s | met |
| API reference data: render the API pages, wall under 60 s | 4.0 s | met |
| API reference data: render the API pages, peak RSS under 2.5 GB | 1.1 GB (1.4 GB writing the cache) | met |

GC modes, measured on head (clean builds without the cache, runs interleaved, median of 3, wall and peak RSS):

| GC | synthetic 32000 | API reference pages |
|---|---|---|
| non concurrent workstation (default) | 6.4-6.6 s, 1.08 GB | 4.3 s, 1.0-1.2 GB |
| concurrent workstation (`DOTNET_gcConcurrent=1`) | 7.6 s, 1.37 GB | 4.5 s, 1.17 GB |
| `DOTNET_gcServer=1` | 6.7 s, 1.08 GB | 4.3 s, 1.05 GB |
| Server GC (DATAS) published with `-p:ServerGarbageCollection=true` | 6.3-6.4 s, 1.35-1.46 GB | 2.9-3.3 s, 1.1 GB |

`DOTNET_gcServer=1` changes nothing measurable on the native AOT binary, which is linked with the workstation GC
(`libRuntime.WorkstationGC.a`): the "Server GC with DATAS" rows of the phase 5 table and of the .NET 11 measurements
used it, so they measured the workstation GC. Server GC compiled in (`libRuntime.ServerGC.a`) is 24 to 32% faster on the
API reference pages and ties on the synthetic site, at the cost of a higher peak (1.46 GB on the synthetic site, at the edge of
its target). The non concurrent workstation GC stays the default here, since this change is about memory; switching to
Server GC is a decision of its own (finding 8).

### Images (NetVips), 2026-10-22

Apple M4 Pro (14 cores), native AOT with the Satori GC (the standard GC in parentheses), clean builds without the cache.
The input is a synthetic set of 200 JPEG photos, 1600x1200, about 1.2 MB each (gradients with a quarter of every row
noise, encoded at quality 85), through `images: { inputs: "img/**/*.jpg", uses: images }` and `write`:

| Variants | Wall of `images` | Variants per second | Peak RSS |
|---|---:|---:|---:|
| `widths: [640, 1280], formats: [webp]`: 400 variants and 200 originals | 2.7 to 3.0 s | about 140 | 0.8 GB |
| `widths: [400], formats: [webp]`: 200 variants and 200 originals | 0.5 s | about 400 | 0.47 GB |

The 400 px case matches the NetVips check (about 400 thumbnails per second, 1600x1200 JPEG to 400 px WebP, libvips
at one thread per operation and units in parallel); encoding 1280 px WebP is what costs in the first row. Both GCs
produce byte identical files. Social images (`social-image`, the SVG template rasterized at 1200x630 and two Pango text
blocks with a font file): 100 pages in 1.2 s, about 12 ms per image, mostly serial (libvips renders text under a global
lock); the first run on a machine takes 1.6 s more while fontconfig builds its cache.

Tool packages (`dotnet pack -r osx-arm64 -p:PackTool=true`): 17.3 MB before, 25.6 MB with `libvips.42.dylib` (17.9 MB
uncompressed); the binary grows by 66 KB (16.82 to 16.88 MB). The `any` package grows by 65 KB (the managed NetVips
assembly) and has no native library.
### Phase 6: watch mode, 2026-10-22

Native AOT with the Satori GC, M4 Pro. **Base** is ab9088a (bounded channels, Satori), **head** phase 6. The machine
was loaded by other builds during these runs (load average 6 to 19), so the variance is higher than in the tables above:
one head run of the synthetic clean build took 6.5 s and is left out. Median of 3, two rounds each.

| Scenario | Variant | Wall (s) | User (s) | Sys (s) | Peak RSS (MB) |
|---|---|---:|---:|---:|---:|
| synthetic 32000, clean, no cache | base | 4.59-4.78 | 9.0-9.3 | 17.6-18.7 | 1987-2012 |
| synthetic 32000, clean, no cache | head | 4.59 | 8.5 | 17.9 | 1620 |
| synthetic 32000, one page edited, disk cache | base | 0.96-1.21 | 3.9-4.7 | 0.7-1.1 | 705-732 |
| synthetic 32000, one page edited, disk cache | head | 0.97 | 4.6 | 1.1 | 706-715 |
| API reference pages, clean, no cache | base | 2.02-2.22 | 5.4 | 7.9-8.4 | 1301-1319 |
| API reference pages, clean, no cache | head | 1.98 | 4.7 | 9.0-9.2 | 1229-1278 |

| Scenario | Wall (s) |
|---|---:|
| synthetic 32000, one page edited, `douze watch` (median of 10 rebuilds, two sessions) | 0.49 |

A clean build costs less CPU and memory: parsed includes were never cached (plan 2.8), so every page parsed the three
includes of the synthetic layout again. A rebuild in a watch session takes half the time of a build from the disk cache:
the session keeps the index, the source stats and the metadata of every page in memory, commits in place, and does not
list the source folder again. Before the work on replays (trace of one rebuild, at about 0.78 s): 130 ms to list, stat
and match the sources, 250 ms of collections (reading the metadata of 32k pages from the packs), 280 ms replaying the
pages through five steps, 65 ms rebuilding the index; after: 10 ms, 170 ms, 250 ms and 8 ms. The rest is the
collections barrier (`collect` runs again whenever a page's metadata changed, and a real edit changes its date) and
about 8 µs per replayed unit.

Against section 9.2's watch target, "synthetic 32k, one page edited, watch mode: under 500 ms to the written output": 0.47
to 0.52 s, at the edge of it on this loaded machine.

### Locale copies, source scan and written contents, 2026-10-22

The target site: about 660 pages in 15 locales, every untranslated page copied into the 14 other locales by
`localize` (`fallback: true`), API reference pages, a code highlighting worker (`run:`, `mode: worker`) after `headings`,
`layout`, `write`, and a search indexer (`run:` after `write`): 70k files written. Native AOT with the Satori GC, M4 Pro,
clean builds with `--no-cache`. **Base** is 15643dc, **head** the three changes below. The machine was heavily loaded by
other builds, a virus scanner reading every written file and other agents (load average 8 to 45), so walls varied by a
factor of 3 from run to run; the table gives interleaved runs on a copy of the site (median of 3, douze process CPU
measured apart from the worker and the indexer) and the unit time per step from `--trace-units` on the site itself.

| Measure | Base | Head |
|---|---:|---:|
| Wall, whole build (copy of the site, loaded machine) | 130-163 s | 102-123 s |
| CPU of the douze process | 303-326 s | 307-325 s |
| Peak RSS | 19.5-19.9 GB | 5.5-6.1 GB |
| First unit after launch (site folder with a stale output folder and a 168k file worker cache) | 2.0-3.6 s | 0.06 s |
| Unit time of `render` (sum of unit spans, two runs) | 349-511 s | 42-43 s |
| ... of which the locale copies | 323-472 s | 35-39 s |
| Units executed | 213,004 | 185,664 (27,340 shared) |

Outputs are byte identical (59k files compared by hash, the search index left out; `rss.xml` dates "now").

- **Locale copies share the units of their page** (finding 11). 6,563 of the 7,546 copies take the render of their page;
  the 983 others read their locale (translated strings, links) and render. `markdown`, `headings` and `replace` share
  too (about 6,900 each). `layout` reads the locale on every page of this site and runs per copy, as it must.
- **The source scan lists only the folders the `inputs` can match** (finding 12): the site folder holds 236k files, of
  which `.douze/` (a worker cache, 168k), a stale output folder (61k) and `node_modules` (6k) are never sources.
- **`write` no longer keeps contents alive** (finding 13): 20 GB of peak RSS was the search indexer's input.

The CPU of the douze process does not drop with the render time: the spans of a unit measure wall time, and on this
machine most of a unit's time was spent waiting (the worker's queue, the thread pool, contention with the indexer and
the scanner); the CPU of single runs ranged from 170 to 330 s for the same binary. The code highlighting worker cannot
share the units of the copies: it reads `lang` (the labels of its copy buttons are translated) and a worker cannot
report what it read.

### Memory of the target site, 2026-10-22

The target site with its current workflow (the native `highlight` and `hover-typescript` steps, no Node worker), clean
builds with `--no-cache`, M4 Pro (14 cores, 48 GB), native AOT, one run each (the machine was otherwise idle). *Footprint*
is macOS's `peak memory footprint` (dirty memory, compressed or not); *max RSS* also counts the pages malloc freed and
macOS reclaims on demand, so it overstates the memory a container would need. **Base** is ca78c8d.

| GC | Variant | `-p` | Wall (s) | Max RSS (GB) | Footprint (GB) |
|---|---|---:|---:|---:|---:|
| Satori | base | 14 | 48.3 | 12.5 | 22.0 |
| Satori | head | 14 | 32.0 | 6.6 | 5.3 |
| Satori | head | 4 | 45.7 | 5.9 | 4.5 |
| standard | base | 14 | 43.4 | 15.6 | 14.5 to 15.1 |
| standard | base | 2 | 147.0 | 13.5 | 13.3 |
| standard | head | 14 | 46.8 | 5.3 | 3.9 |
| standard | head | 4 | 59.2 | 4.6 | 3.4 |

The outputs are the same as the base's (70,734 files compared by hash), but for files that differ between two builds
of the base too (`rss.xml`, some locale copies, some API reference pages: finding 14).

Where the memory went, from a sampler of `GC.GetGCMemoryInfo` and full heap dumps (`dotnet-dump`; `dotnet-gcdump` stops
at 10 million objects and misses most of this heap):

- **Lowering `-p` did not lower the peak of the base** (13.3 GB at 14, 13.5 GB at 2): the peak was what the build kept,
  12.4 GB of GC heap, 10 GB of it in the large object heap: the 48,000 API reference pages and their Markdown twins.
  `write` waited for every reduce step upstream of it to publish its contribution, `hover-typescript` among them (an
  11 s barrier on the articles), although it reads no scopes. Its channel from `api-layout` was unbounded for that
  reason (`Scheduler.GetCapacity`): a full channel would have stopped the API branch, which shares `site-data` with the
  articles. By the time `write` started, the API branch had rendered almost everything. A step now says whether it reads
  scopes (`Step.ReadsScopes`, false for `write`): one that does not starts at once and its channels are bounded. GC heap
  peak 12.4 to 3.3 GB.
- **What waits is UTF-8.** A content entering an unbounded channel or the input of a reduce or group step is compacted
  (`FileContent.Compact`): a text drops its UTF-16 string for its UTF-8 bytes, in blocks of 64 KB, below the 85 KB of the
  large object heap, which is collected only with the whole heap and not compacted. A step that reads it decodes it
  again. `hover-typescript` and `hover-csharp` (`CodeFenceStep`) kept the text of every page with a fence until the end of
  their unit, and built every output before returning them: they read a page again to write its results, and compact
  their outputs.
- **Native memory: Oniguruma.** About 800 MB of malloc at `-p 14` (`MallocStackLogging` and `malloc_history`): the
  regexes of the TextMate grammars, compiled once per registry, and the highlighter kept one registry of a language per
  parallel unit. libvips holds about 10 MB. A language now has 4 registries at most (`CodeTokenizer`); a unit waits
  for one when they are all in use, which costs nothing measurable here.
- **The live set at the peak** (head, standard GC, JIT, mid-build): 1.6 GB of a 3.1 GB heap; the rest is garbage, mostly
  the transient strings of rendered pages. Of the live set, the API reference data as `Node` trees is about 450 MB
  (78 MB of JSON: 5 million strings, and a `Dictionary` per object, 190 MB of dictionaries alone), the pages waiting at
  barriers about 400 MB before compaction, the registries' managed side 130 MB, and the scheduler's refs, dependency
  sets and unit results about 150 MB.
- **The Satori GC keeps about 1.5 GB more** than the standard GC on this site, for a third less wall time. It does not
  follow `GCHeapHardLimit` (set at startup or with `GC.RefreshMemoryLimit`): its footprint stays at 5 GB with a 2.25 GB
  limit.

Tried and left out: a store that moves the contents waiting at barriers to a temporary file beyond a memory budget (a
`--memory` option, which also set `GCHeapHardLimit`). Once `write` stopped waiting and waiting contents became UTF-8,
nothing reached the store, even with a 3 GB budget; a 2 GB budget ran out of memory in the live set, not in waiting
contents. Lowering `-p` is the way to use less memory.

What is left, by size: a compact representation of data nodes (keys shared, small objects without a `Dictionary`,
repeated strings once) for the 450 MB of data; rendering into UTF-8 blocks instead of a string, for the large object heap
churn of rendered pages; image variants, memoized as bytes for the whole build (`ImageProcessor._variants`).
### Images: NetVips or SkiaSharp, buffers or streams, 2026-10-22

Is SkiaSharp lighter than libvips for Douze's image work, can images stream from source file to output file instead of
going through whole buffers, and what does `ImageProcessor._variants` (every encoded variant kept for the build) cost?
`bench/Douze.ImageBench` runs both libraries on the same inputs, each run in its own process:

```bash
# <sources>: PNG and JPEG files, searched recursively; <singles>: a few files run one at a time; <svg>: a 1200x630 SVG
# whose text contains {{n}}; outputs go under <scratch> and are deleted as it goes.
bench/image-bench.sh <sources> <singles> <svg> <scratch>
```

- **Buffer** is Douze's path: the source read into a `byte[]`, a variant encoded into a `byte[]`, hashed, written.
  libvips as `ImageProcessor` sets it up (`thumbnail`, one thread per operation, no operation cache, JPEG flattened
  on white, `keep=none`). SkiaSharp 4.153.1: `SKCodec` decoding at the smallest scale not below the target (JPEG n/8,
  nothing for PNG), to sRGB, then a draw with mipmaps for reductions of 2 or more and a Mitchell cubic otherwise, and
  its WebP and JPEG encoders at the same quality, without metadata. **+memo** also keeps every encoded variant until
  the end, as `_variants` does.
- **Stream**: the source file opened with a 16 KB buffer (`Image.ThumbnailStream`, `SKCodec.Create(Stream)`), the
  encoder writing to a stream that hashes what it writes into a temporary file, renamed to the hashed name at the end
  (`HashingFileStream`; `Image.WriteToStream`, `SKPixmap.Encode(SKWStream)`).
- Measures: managed allocations (`GC.GetTotalAllocatedBytes`), native malloc in use above the start (every malloc zone,
  polled every 5 ms: an upper bound, since it counts allocated pages that were never touched), libvips' own high water
  mark (`vips_tracked_get_mem_highwater`) and the peak physical footprint (`/usr/bin/time -l`, which includes the
  process' baseline: about 33 MB with SkiaSharp loaded, 43 MB with libvips). `MallocStackLogging` and `malloc_history`
  were not needed: the zone statistics give the peak directly. JIT build, after a warmup pass.

Inputs: the 573 PNG and JPEG files of the target site (132 MB: screenshots, median 112 KB, the largest a 2880x2048 PNG
of 4.6 MB and a 3706x5559 JPEG of 10 MB), and its social card SVG (a 1200x630 card with an embedded PNG background, a
gradient and a text pill). Apple M4 Pro (14 cores), loaded by other builds (load average 15 to 25), so walls are
indicative.

**Per operation** (`-p 1`, 5 repeats; time per operation, then managed allocations per operation / native malloc peak /
peak footprint):

| Source, variant | vips buffer | vips stream | skia buffer | skia stream |
|---|---:|---:|---:|---:|
| 10 MB JPEG 3706x5559 → webp 640 | 128 ms, 10.1 / 24 / 73 MB | 132 ms, 0.1 / 14 / 46 MB | 130 ms, 10.1 / 14 / 66 MB | 124 ms, 0.0 / 12 / 35 MB |
| 10 MB JPEG → webp 1280 | 242 ms, 10.2 / 48 / 76 MB | 238 ms, 0.2 / 33 / 55 MB | 240 ms, 10.2 / 18 / 103 MB | 238 ms, 0.1 / 27 / 59 MB |
| 10 MB JPEG → jpeg 1280 | 138 ms, 10.3 / 32 / 85 MB | 146 ms, 0.3 / 21 / 60 MB | 138 ms, 10.3 / 21 / 105 MB | 142 ms, 0.0 / 21 / 55 MB |
| 4.6 MB PNG 2880x2048 → webp 640 | 62 ms, 4.7 / 24 / 60 MB | 62 ms, 0.1 / 19 / 53 MB | 68 ms, 4.7 / 38 / 78 MB | 58 ms, 0.0 / 38 / 61 MB |
| 4.6 MB PNG → webp 1280 | 96 ms, 4.7 / 28 / 62 MB | 112 ms, 0.1 / 23 / 55 MB | 104 ms, 4.7 / 43 / 81 MB | 98 ms, 0.0 / 41 / 68 MB |
| 4.6 MB PNG → jpeg 1280 | 58 ms, 4.7 / 20 / 54 MB | 54 ms, 0.1 / 16 / 46 MB | 66 ms, 4.7 / 48 / 82 MB | 64 ms, 0.0 / 48 / 66 MB |
| 110 KB PNG (median) → webp 640 | 18 ms, 0.1 / 11 / 44 MB | 12 ms, 0.0 / 12 / 44 MB | 12 ms, 0.1 / 9 / 36 MB | 18 ms, 0.0 / 10 / 37 MB |
| 110 KB PNG → webp 1280 | 36 ms, 0.1 / 11 / 44 MB | 30 ms, 0.0 / 10 / 46 MB | 46 ms, 0.1 / 18 / 37 MB | 26 ms, 0.0 / 18 / 39 MB |
| 100 KB JPEG 800 wide → webp 800 | 48 ms, 0.1 / 7 / 46 MB | 42 ms, 0.1 / 10 / 48 MB | 48 ms, 0.1 / 23 / 48 MB | 44 ms, 0.0 / 24 / 49 MB |

**Batch**: the 573 sources to 640 and 1280 wide WebP (1,146 variants, 26 MB), one source per task:

| | Wall | Allocated | Native malloc peak | libvips peak | Peak footprint |
|---|---:|---:|---:|---:|---:|
| vips buffer, `-p 4` | 8.7 s | 163 MB | 103 MB | 72 MB | 104 MB |
| vips buffer+memo, `-p 4` | 8.6 s | 164 MB | 97 MB | 67 MB | 130 MB |
| vips stream, `-p 4` | 8.6 s | 62 MB | 98 MB | 66 MB | 103 MB |
| skia buffer, `-p 4` | 8.6 s | 159 MB | 150 MB | | 121 MB |
| skia buffer+memo, `-p 4` | 8.5 s | 159 MB | 148 MB | | 153 MB |
| skia stream, `-p 4` | 9.2 s | 43 MB | 184 MB | | 125 MB |
| vips buffer, `-p 14` | 3.8 s | 164 MB | 207 MB | 132 MB | 125 MB |
| vips buffer+memo, `-p 14` | 3.7 s | 164 MB | 212 MB | 128 MB | 123 MB |
| vips stream, `-p 14` | 3.9 s | 63 MB | 178 MB | 122 MB | 111 MB |
| skia buffer, `-p 14` | 3.8 s | 160 MB | 290 MB | | 139 MB |
| skia buffer+memo, `-p 14` | 3.6 s | 160 MB | 273 MB | | 173 MB |
| skia stream, `-p 14` | 3.8 s | 45 MB | 329 MB | | 124 MB |

**Social card SVG to a 1200x630 PNG**, 100 cards (librsvg through libvips; Svg.Skia 5.2.3, a managed SVG reader
drawing on SkiaSharp):

| | Wall | Allocated | Native malloc peak | Peak footprint |
|---|---:|---:|---:|---:|
| vips, `-p 4` | 0.93 s | 170 MB | 79 MB | 100 MB |
| skia, `-p 4` | 1.52 s | 758 MB | 46 MB | 180 MB |
| vips, `-p 14` | 0.53 s | 170 MB | 213 MB | 230 MB |
| skia, `-p 14` | 0.68 s | 763 MB | 113 MB | 282 MB |

Outputs: the buffer and stream paths of a library write byte identical files. Between the libraries, the WebP variants
are 5% smaller with SkiaSharp, 9 of 1,052 are one pixel shorter (another rounding of the height), and the pixels
differ: PSNR median 37.6 dB, lowest 27.6 dB on screenshots of console text, which SkiaSharp's mipmapped reduction
renders softer than libvips' lanczos3. The cards match closely (42.7 dB) and are 12% larger with SkiaSharp.

**The variant memo on the target site** (`douze -s . -o <out> --no-cache --plain`, native AOT with the Satori GC, on
the memory budget branch, b6f296c): `_variants` ends the build with 430 variants holding **19.6 MB**, against a peak
footprint of 4.8 to 5.1 GiB; the social image rasters (`_rasters`) hold 29 backgrounds, 88 MB, within their 256 MB
budget. A prototype that memoizes only the hash, length and size, gives the bytes to the first page that asks and
gives the other pages content that encodes the variant again if it is read (`write` and the cache compare hashes, so
it is read only when such a page reaches them first) changed nothing measurable, and encoded 8 to 108 variants twice
per build:

| Build (interleaved) | Run 1 | Run 2 | Run 3 |
|---|---:|---:|---:|
| Head: memo with bytes | 30.6 s, 5.09 GiB | 28.5 s, 4.77 GiB | 29.9 s, 4.89 GiB |
| Memo without bytes | 36.9 s, 4.85 GiB (108 encoded again) | 29.5 s, 5.02 GiB (9) | 29.0 s, 5.10 GiB (8) |

**Recommendation.**

- **Stay on NetVips.** SkiaSharp is no lighter: the same time on variants (both libraries spend it in libjpeg-turbo,
  libpng and libwebp), more native memory on PNG sources (it decodes the whole image, where libvips' sequential
  `thumbnail` keeps a few lines: 38 to 48 MB against 16 to 24 MB on the 2880x2048 PNG), 1.6 times slower and 4.5
  times the managed allocations on SVG, and no text layout: the social images' Pango blocks (wrapping, alignment,
  font files) would have to be rebuilt on HarfBuzzSharp. Switching would also change every image the sites publish.
- **Don't stream.** It is byte identical and saves the source and the variant buffers (100 MB of managed allocations
  over the 573 sources, 26 MB of footprint on the 10 MB JPEG, 1 to 14 MB on the batch), but the target site's image
  work is a few hundred MB of allocations in a build that peaks at about 5 GB: the cost is elsewhere. In Douze it would
  also mean variants bypassing `FileContent` and `write` (an output written by the image step, outside the cache's
  blobs), which the engine is not built for. What is cheap is done: `ImageSources.ReadAsync` reads a source once into
  an array of its length (it went through a `MemoryStream` and `ToArray`, two copies on every render that uses an
  image, and a third in `social-image`).
- **Keep the memo as it is.** 20 MB is 0.4% of the peak, and the memo without bytes needs a second encode whenever a
  page that did not take the bytes reaches `write` first; the prototype is not merged. If the memo ever matters (a
  site with thousands of large variants), the better fix is a `FileContent` that reads the written output file instead
  of encoding again.

### Small Markdown posts: the SSG comparison's blog, 2026-10-23

`bench/ssg-comparison` builds a blog of 250 to 4000 posts of about 1 KB (the Douze version of eleventy-base-blog), so
fixed and per-file costs dominate. Profiled with `--trace --trace-units`, the Time Profiler (`xctrace`) on the native
AOT binary on macOS, and `strace -c` and a file system micro-benchmark in the Linux container of the comparison. Before
and after: `bench/ssg-comparison/README.md` (4000 posts, clean build: 0.61 to 0.37 s in Docker under load, 0.54 to
0.37 s with the first change on a quiet machine; macOS: 0.74 to 0.59 s).

Where a clean 4000-post build went, before: startup and the cache-less setup 15 ms; scanning and reading the sources,
front matter and permalinks 45 to 130 ms (the scan is bound by opening files, not by parsing: 40 ms of CPU in all);
`collect` 5 ms; then render, Markdown, layout and write streaming together. Of the units' CPU, layout took 3.4 s (the
previous and next loop, finding 15), write 5 to 10 s, Liquid, Markdown and paginate under 0.2 s in all. Once the loop is
gone, the writes are the critical path (finding 16).

Measured and not adopted:

- **`OptimizationPreference=Speed`** for the native AOT compiler: within noise on the clean build (Docker: 0.316 against
  0.315 s, macOS: 0.582 against 0.625 s with a spread of 0.05 s) and the warm rebuild (0.147 against 0.148 s), for a
  binary 1 MB larger. The build is bound by the file system and the scheduling, not by code generation.
- **Opening and writing with libc directly** (`open`, `write`, `close` instead of .NET's `File.OpenHandle`, which adds
  `fstat`, `fstatfs`, `flock` twice, `ftruncate`): 157 against 183 ms for 4000 files in their own folders at 4 threads
  in Docker, 233 against 247 ms on macOS. Not worth platform-specific interop. Disabling .NET's file locking globally
  (`System.IO.DisableFileLocking`) would remove the same calls, but the build cache and the tool installer rely on
  `FileShare.None` locks between processes.
- **Fewer folders.** Writing 4000 files to one folder takes 25 ms against 170 ms with a folder per page (Docker): the
  `mkdir` is most of the cost. The folder per page is what pretty urls need, so it stays.

### Docs-heavy pages: highlighted fences, headings, sidebar, 2026-10-23

The docs-heavy scenario of the SSG comparison (on the docs-site branch: `run-docs-heavy.sh`, `scenarios/docs-heavy/`):
pages of 6 to 10 KB with 3 to 6 fences in 7 languages (bash, ts, html, json, yaml, js, csharp; every fence unique),
heading ids, a 50-page sidebar and a TOC, highlighted with one theme. Its runs: Douze 1.08, 3.09 and 12.4 s for 1000,
4000 and 16,000 pages, Hugo 0.41, 1.31 and 5.7 s, Eleventy and Astro (Shiki) 6.9 and 16.8 s at 4000.

At 4000 pages (17,892 fences, 266,941 code lines), the Time Profiler gives highlight 87% of the CPU, and Oniguruma's
`onig_search` alone 75%: TextMateSharp's line tokenizer searching each pattern of the current rule from the current
position. That is 1.5 ms of CPU per fence, 100 µs per code line. Onigwrap converts offsets with
`Encoding.Unicode.GetByteCount` on every search (5% of the build's CPU, where `2 * chars` would do). Next: write 3.5%,
the GC 3%, `CodeBlockBuilder` 3%, Markdown, Liquid and layout under 2% each, `headings` 50 µs per page. Units use 9 to 10
cores out of 14 in Docker, as Hugo does.

- **More registries per language does not help**: `MaxRegistriesPerLanguage` at 8 or 14 instead of 4 gives the same
  time (3.4 to 4.2 s on macOS under load, either way) for 1.1 to 1.3 GB of peak RSS instead of 0.76 GB.
- **The write and read changes of findings 16 and 17 are neutral here** (Docker, interleaved runs at 4000 pages: 2.98
  to 3.12 s with and without the read change, the same as the scenario's own run).
- **Memory at 16,000 pages**: peak RSS 1.6 GB on Linux (sampled `VmRSS`), 1.45 to 1.5 GB on macOS, 12.6 to 13.8 s on
  macOS, linear from 4000 pages. The 12.4 to 21.3 s spread of the scenario's runs is not memory (the VM has 7.7 GB);
  the host was loaded by other builds (load average 9 to 40 during these measurements).

Speculative, not done:

- **Onigwrap's offsets** (`2 * chars` instead of `GetByteCount`, a reused region instead of a `Region` per search):
  about 5%, a change to a third party package (TextMateSharp's Onigwrap).
- **A memo of tokenized fences per build**, keyed by grammar and code: nothing here (every fence is unique), but real
  documentation repeats its install and setup snippets; tokens of every fence would stay in memory until the build ends.
- **A faster tokenizer than TextMate grammars** (Hugo's Chroma lexers are hand-written regex tables): the 2.3x gap to
  Hugo is the regex engine, not Douze's pipeline. Shiki-compatible output is the reason for TextMate.

Since then, the highlight step tokenizes with [Tint](https://github.com/sebastienros/tint) on GregsEx (managed regexes)
instead of TextMateSharp on Oniguruma, so the Oniguruma findings above (native memory, `onig_search`, Onigwrap's
offsets, registries per language) describe the old engine. Clean builds of aspire.dev (32,385 pages, 8,326 with fences,
`--no-cache`, interleaved, macOS M4 Pro under a load of 5 to 18), Douze 1.0.0-preview.48 against the same commit with
Tint:

| Engine | Wall | User CPU | `highlight` done at | Peak RSS |
|---|---:|---:|---:|---:|
| TextMateSharp | 33.3 s, 31.1 s | 270 s, 265 s | 19.3 s, 18.2 s | 15.9, 15.5 GB |
| Tint | 18.5 s, 17.7 s | 158 s, 155 s | 6.8 s, 6.1 s | 14.5, 15.5 GB |
| Tint, C#, TypeScript, HTML and JSON as kernels | 20.6 s, 18.8 s | 159 s, 157 s | 6.7 s, 6.3 s | 14.3, 14.2 GB |

The peak RSS of that table is the workflow's, not the engine's: aspire.dev then had one `highlight-chrome` step (the
code of the chrome, highlighted after the layouts) that needed both `layout` and `api-layout`. A step that reads scopes
waits for every reduce step upstream of it, so the API pages piled up unbounded in front of it while the articles'
`hover-typescript` and collections ran: the barrier `write` no longer has (finding of 2026-10-22 above). With one chrome
step per branch, each waiting only for its own branch's reduce steps, the peak is back to what that finding measured:
TextMateSharp 33.3 s, 6.9 GB RSS (5.5 GB footprint); Tint 18.7 and 19.9 s, 7.2 and 8.0 GB RSS (5.8 and 6.5 GB). A step
that joins branches pays for the reduce steps of all of them.

Kernels (the `DouzeKernelGrammars` opt-in of 0.3.x) gained nothing measurable here, as Tint measured (3 to 4% on large
C# files), for 5 MB more binary; with GregsEx 0.5.2's kernels for subexpression calls (Tint 0.1.1) a clean build of
aspire.dev was still within noise (20.7 to 23.0 s either way: the regex engine is about 6% of its CPU, and kernels cut
that by a fifth), so the opt-in was removed in 0.3.2. The pages are byte-identical with and without kernels. Against TextMateSharp, VS Code's
grammars, which now win over tm-grammars', change YAML (`: ` is one token) and dotenv (numbers are not colored); C#,
TypeScript, JSON, XML, shell and PowerShell are unchanged.

## Findings

Measured on the engine of phase 1b. None of these are fixed here (phase B owns only `bench/`); they feed phase 5.

1. *Fixed by Fluid 3.0.0-preview-780 (`IFluidArray`), see above.* **`{% for p in collections.all limit: 50 %}` costs O(N) per page, O(N²) per build.** The nav include alone takes
   the synthetic 32k build from 6.9 s to 39.5 s and from 3.4 GB to 6.0 GB; with `collections.all[i]` for i in 0..49
   the same output costs almost nothing (6.9 s against 6.4 s without a nav). Fluid's `for` enumerates the whole
   `NodeArrayValue` (and wraps every item in a `MetadataValue`) before applying `limit` and `offset`. Suggested fix:
   apply `offset` and `limit` while enumerating in Fluid's `ForStatement` (Fluid 3 is still in preview), or let
   `NodeArrayValue` expose a sliced enumeration that the `for` tag uses; either way, wrap items lazily. Eleventy sites
   loop over collections with `limit` all the time, so this is the first thing to fix.

2. *Fixed in phase 5 (Merkle hashes cached on nodes, refs computed in parallel).* **Metadata hashing of paginated pages is O(pages²) per paginated file, and the reduce barrier does it on one
   thread.** `Metadata.Hash` serializes the whole `Own` of a page, which holds `pagination.pages` and
   `pagination.hrefs`, shared by every page of the file. The API reference site has files with up to 2,592 pages
   (Σ pages² = 12.7 million summaries per hash pass); `collections` spends 16 of the build's 23 s in
   `RunReduceAsync` computing `files.Select(x => x.Ref)` with one thread busy in `CanonicalJson.Write` and 13 idle,
   and `permalink` takes another few seconds rehashing the same pages. Suggested fixes: cache a hash on each published
   `NodeObject` / `NodeArray` (they are immutable once published, as `NodeValue<Metadata>` already is through
   `$metadata`) so a shared subtree is hashed once; compute the refs of a reduce or group input in parallel; and
   consider leaving `pages`/`hrefs` out of the per-page hash (they are a function of the source file and its data,
   which are already in the unit's key).

3. *Fixed: phase 5 for results and sources, the bounded channels change for the files kept by every node to detect duplicate paths (see above).* **Every intermediate content stays in memory until the build ends (open question 18 matters).** A heap dump of the
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

6. **Smaller observations.** Loading the API reference data is a single unit of the `data` step: 0.9 s wall for 1.26 s of
   CPU, so parsing the 342 files in parallel would make it about 0.3 s. RSS for the data is about 9 times the JSON size
   (714 MB for 78.5 MB), within the target. The render and layout steps parse each page's template once, which is
   cheap next to findings 1 to 3.

7. *Re-measured in phase 5 and with the bounded channels: non concurrent stays the default (finding 8).* **The GC mode matters as much as the code on .NET 11.** Background GCs throttle allocating threads, which made the
   allocation heavy `limit:` loop 40% slower than on .NET 10 until `douze` turned concurrent GC off (".NET 11 and
   runtime async" above). Re-measure the GC modes (concurrent, non concurrent, Server GC with DATAS) after each memory
   change of phase 5: once findings 1 and 3 cut the allocation rate and the live set, concurrent GC or a lower peak RSS
   from a heap limit (`GCHeapHardLimit`, `GCConserveMemory`) may become the better trade.

8. **Server GC must be compiled in to be measured.** A native AOT binary links one GC: `DOTNET_gcServer=1` has no effect
   on the published `douze` (workstation GC), so the Server GC rows measured before the bounded channels change were the
   workstation GC. Published with `-p:ServerGarbageCollection=true`, Server GC with DATAS builds the API reference pages 24 to 32%
   faster (2.9 to 3.3 s against 4.3 s) and the synthetic site in the same time, with a peak 10 to 35% higher (1.35 to 1.46
   GB on the synthetic site against 1.08). Worth deciding on its own: it trades the memory margin of 9.2 for speed on
   data heavy sites.

9. **Satori GC is faster still, for more memory.** The experimental Satori GC, published with the `PublishWithSatoriGC`
   MSBuild SDK (`-p:UseSatoriGC=true`, see `AGENTS.md`; it replaces the native AOT compiler and runtime with a third party
   build tracking .NET 11 `main`), produces byte identical sites. Interleaved with the default and Server GC on the head of
   the bounded channels change (median of 3, wall and peak RSS):

   | Scenario | non concurrent workstation | Server GC (DATAS) | Satori |
   |---|---|---|---|
   | synthetic 32000, clean, no cache | 6.44 s, 1.07 GB | 6.26 s, 1.41 GB | 5.53 s, 1.88 GB |
   | API reference pages, clean, no cache | 4.41 s, 1.20 GB | 3.35 s, 1.09 GB | 2.88 s, 1.40 GB |
   | synthetic 32000, one page edited | 1.51 s, 0.58 GB | 1.04 s, 0.65 GB | 1.05 s, 0.70 GB |

   On a 500 page synthetic site the three take the same time (0.18 to 0.20 s clean, 0.05 to 0.07 s for an edit) and Satori
   peaks lower than the default (114 MB against 167 MB; Server GC 99 MB). Satori is the default of native AOT builds
   since (`-p:UseSatoriGC=false` opts out), accepting the synthetic 32k site's peak over the 1.5 GB target of 9.2 for
   the speed. A native AOT binary cannot switch to it at run time:
   the compiler emits Satori's write barriers into the code. A binary linked with the Server GC, though, runs as
   workstation with `DOTNET_gcServer=0` (6.5 s, 1.11 GB on the synthetic site) and as Server GC without it.

10. **Images, social images and Markdown twins of the target site.** Measured on the target site (clean builds,
    `--no-cache --trace-units`, unit time summed per step), with the base and this change built back to back on the
    same site; the machine ran other builds at the same time (load average 25 to 30), so the absolute times are
    inflated and only the ratios matter. The outputs are byte identical: the API reference pages and twins (48,155
    files), the pages with their image variants (every file but the dated RSS feed) and the social images (527 files),
    each through a workflow of those steps only, and on the full build the images, social images, dashboard pages and
    a sample of the twins.

    | What | Before | After |
    |---|---:|---:|
    | `{% image %}` page with 58 tags (light and dark), per render (15 renders) | 19.6 s (8.4 s on a quieter machine) | 0.95 to 1.9 s |
    | `render` step, unit time | 573 s | 167 s |
    | Social image (`og`), per card, median (522 cards) | 125 ms (82 ms quieter) | 28 ms |
    | `og` step, unit time | 145 s | 54 s |
    | Markdown twins (`api-render-md`), per page, median (24,096 pages) | 1.49 ms | 0.26 ms |
    | HTML pages of the same data (`api-render`), per page, median | 0.24 ms | 0.22 ms |

    - *Images.* Variants were already memoized for the build, but a page encoded its variants one after the other in
      its render (58 WebP encodes of large screenshots, about 145 ms each), and the locale copies rendered at the same
      time waited on the same `Lazy` blocking a thread pool thread each. Variants are now memoized as tasks run on the
      thread pool, all the variants of a source are started before the first is awaited, and the `liquid` step starts
      the variants of the page's image tags whose arguments are literals before it renders the page
      (`ImageTag.PrefetchAsync`), so a page costs its slowest image rather than the sum. The file names keep the hash
      of the encoded bytes, so the markup still waits for the encodes. The NetVips calls are as they were (one
      `thumbnail` and one save per variant, `keep=none`, no resize at the source's width); WebP encoding is the cost.
      What is left: the copies of a page still render (and wait) once per locale, and tags inside includes and
      shortcodes, or with variable arguments, are not prefetched.
    - *Social images.* Per card, the SVG (with the 400 KB background PNG inlined as base64) was rasterized by librsvg
      and written as a PNG, read back, the text composited, the card encoded. The rasterized SVG is now memoized by
      content and size as raw pixels (`ImageProcessor.Rasterize`, within 256 MB; the cards of the target site share
      a handful of backgrounds, by topic), the text layers take fewer libvips operations (each takes process wide
      locks, `vips_cache_operation_buildp` and the argument table, which 14 parallel cards contend for), and the font
      files and inlined images are read once per build. What is left per card: the Pango text under libvips' global
      text lock, and the PNG encode (deflate at the default compression, a third of the card), which the byte
      identical output pins.
    - *Markdown twins.* The Markdown output path itself costs nothing measurable (`encode: html` or `protect: none`
      give the same times): the twin templates look their module up in route lists of 1,810 to 10,634 items with
      `find_index`, then `slice | where`, a scan per page, O(pages × routes) per build. `where` and `find_index` with
      a string over a data array of 32 items or more are now answered from an index built once per array and member
      (`MemberIndex`, Fluid's answers), and `slice` of a data array is a data array whose item reads are recorded as
      reads of the array's items, so that `where` over a slice uses the array's index.

    Seen on the way, outside this change: the source snapshot scans `node_modules` of the target site with an
    `lstat` per file (`SourceSnapshot.StatAsync`), up to 10 s of a build on this machine, and the full build peaks
    at 15 to 21 GB.

Suggested order for phase 5: finding 2 (hash caching, it also makes cache keys cheap), then 3 (results and content
retention, which the disk cache design decides anyway), then 4 and 5. Finding 1 is a Fluid change and can land at any
time.

11. *Fixed: variants (`FileVariant`) and own reads (`Step.RecordsOwnReads`).* **Locale fallback copies redid every
   step.** `localize` copies each untranslated page into every other locale with only `locale`, `lang`, `dir`, `url`,
   `fallback` and `original_url` changed, and every map step after it ran one unit per copy: on the target site 213 s
   of `render`'s 229 s and 195 s of the code worker's 210 s were copies. A page and its copies are now variants, and a
   step whose units record every key of their file's own metadata they read (`liquid` pages, `markdown`, `headings`,
   `replace`) runs the first variant to arrive and gives the others its outputs, with their own keys and path prefix,
   when the unit read none of the keys they differ by. Copies wait for their leader without holding a slot. Left: a
   `run:` step cannot say what it read, so the code worker still runs every copy (its output differs per locale on this
   site anyway); a worker protocol field listing the metadata keys a result depends on would let a worker share too.
   Which variant leads is a race, so the count of shared units can vary by a few between builds when a page reads a key
   only some copies have (`fallback`); the outputs do not.

12. *Fixed: `SourceScope`.* **The source scan listed and stat'ed the whole source folder.** On the target site that is
   236k files for about 3k sources, 2 to 4 s before the first unit (the cache folder was excluded only when it was the
   default `.douze/cache`, so `--cache-dir` elsewhere let the worker caches under `.douze/` in). The scan now walks the
   folders the `inputs` patterns start with, never `.douze/` nor the output and cache folders, and prunes them instead of
   listing their files; command `sources:` are listed the same way. A watch session rescans when the workflow reads a
   folder its snapshot does not cover.

13. *Fixed: `FileContent.FromWritten`.* **A step after `write` held every content of the site.** The search indexer of
   the target site `needs: write`, so it receives all 70k written files, and holds them until `write` is done: 20 GB of
   peak RSS (17.9 GB of GC heap; 2 to 3 GB without the indexer). `write` now passes a written file on with its content
   read back from the output folder on each access (same hash and length, so the same ref), and a later `write` step
   does not write such a file onto itself. Peak RSS: 6 GB. Left: a replayed `write` unit restores its outputs with the
   input's content, so a rebuild from the cache still holds the contents of what reaches the indexer; the Satori GC
   peaks higher than the standard GC on the same build (17 to 21 GB against 10 to 14 GB before this change).

14. *Fixed: the arguments of Fluid's filters, `CodeTokenizer`'s time limit.* **Some pages of the target site differed
   between two clean builds of the same binary** (about one build in three at `-p 14`, a few pages each: locale copies,
   API reference pages, a page with a shell code block). Two causes, neither in the scheduler:
   - *Fluid cached the arguments of a filter that were not literals.* A filter call caches its arguments when they are
     all literals, but the flag was a read then a write on every evaluation without cached arguments: when the first
     renders of a template run on several threads at once, one could write `true` back after another wrote `false`,
     and that one cached its arguments for every later render. On the target site `dirs | slice: 0, n` (the common
     source folder of a C# package page) and `s.name | slice: 3, s.name.size` (the setters of a TypeScript handle:
     `readonly`, `get · set`) took another page's values; the locale copies that differed were most likely the same
     race in the templates they share (they did not differ again with the fix). Fixed in Fluid
     (sebastienros/fluid#1067, in 3.0.0-preview-789).
   - *A highlighted line stopped at 500 ms.* `CodeTokenizer` gave the rest of a line that took longer the style of
     where it stopped, and how long a line takes depends on the load of the machine: a new registry compiles its rules
     on its first lines while the other units run (`aspire add redis` came out as one token in some builds). The
     limit is now 30 s, and a line that reaches it fails the build instead of being styled differently.

   With both, six clean builds in a row at `-p 14` gave the same bytes for all 60,036 files but `rss.xml` (dated
   now). The variant leader race of finding 11 did not show: the locale copies that differed were the Fluid race.

15. *Fixed: the `previous_item` and `next_item` filters.* **A Liquid loop over a collection on every page is O(n²).**
   The SSG comparison's post layout found its previous and next posts by looping over `collections.posts` until its own
   url: 3.4 s of CPU at 4000 posts, a third of the wall time. The filters index a collection by url once (published
   nodes never change) and return a neighbor; the page depends on its own item and the neighbor instead of the whole
   collection, so changing a post's title renders 3 pages instead of all of them (0.41 to 0.18 s at 4000 posts). The
   cost: a warm rebuild with nothing changed checks 4000 dependency sets instead of one shared set (0.093 to 0.122 s).

16. *Fixed: `PhysicalFileSystem.WriteFileAsync`.* **Writing outputs with every core is slower than with four.** Creating
   a folder and a file per page contends in the kernel (the parent folder's lock, the journal): 4000 pages in their own
   folders take 170 ms on 1 to 4 threads and 300 ms on 14 in Docker, 250 against 630 ms on macOS. `write` units filled
   the 14 threads of the build. Output files are now written 4 at a time (`MaxConcurrentWrites`), synchronously (an
   asynchronous `FileStream` on Unix is the same blocking call from another thread pool item), and each folder is
   created once. 4000 posts, clean build: 0.359 to 0.330 s in Docker, 0.73 to 0.63 s on macOS. A cap of 2 measured the
   same as 4 in Docker; 4 is better on macOS.

17. *Fixed: `IFileSystem.ReadFileAsync`.* **Sources were read through an asynchronous stream and a `MemoryStream`**: an
   existence check, a thread pool hop per read, two copies. They are read with `File.ReadAllBytes`. A few ms at 4000
   posts in Docker (0.330 to 0.325 s), within noise on macOS. Left: opening 4000 sources still takes most of the 45 to
   130 ms before the first page renders, on macOS especially; the scan could read them with fewer threads too, which
   was not measured.
