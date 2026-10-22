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

Suggested order for phase 5: finding 2 (hash caching, it also makes cache keys cheap), then 3 (results and content
retention, which the disk cache design decides anyway), then 4 and 5. Finding 1 is a Fluid change and can land at any
time.
