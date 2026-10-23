---
title: Memory and parallelism
description: How many units Douze runs at once, how memory grows with parallelism, and how to build in a container with little memory.
---

Douze runs the steps of a workflow at the same time, and the units of each step in parallel: as many as the machine
has cores, by default. That makes a build fast, and it also sets how much memory the build holds. This page explains
what uses memory, how to trade speed for memory with `-p`, and what to set in a container.

## Parallelism

`-p` (`--parallelism`) is the most units that run at once, across all steps. Its default is the number of
processors. `site.parallelism` in `douze.yml` sets it for a site, and `-p` wins over it:

```yaml title="douze.yml"
site:
  parallelism: 8
```

```sh
douze -p 4
```

Steps do not wait for each other to finish: a page goes on to `markdown` as soon as `render` is done with it, while
other pages are still rendering. Filter command steps run up to `-p` processes at once. The hover steps have their own
limit, `processes` (4 language servers by default).

## What uses memory

Each unit that runs holds its working set: the page it renders and its transient strings, the regexes of the code
grammars it uses, the image it encodes. At `-p 14`, about 14 of them are in memory at once. Whatever the parallelism,
a build also holds:

- the site's data and the data of every page;
- the pages that wait at a barrier, a step that needs every page before it can run (`collect`, `localize`, a batch
  command step). They wait as UTF-8 bytes.

So lowering `-p` lowers the part that grows with parallelism, and the build takes longer. It does not lower what the
site's data and barriers need.

## Figures

The [benchmarks](benchmarks.md) measure a documentation site of 70,000 files with 78 MB of JSON data, on 14 cores,
with highlighting and TypeScript hovers. Footprint is the dirty memory that macOS reports at the peak.

| `-p` | Time | Peak footprint |
|---:|---:|---:|
| 14 | 32 s | 5.3 GB |
| 4 | 46 s | 4.5 GB |

The repository's `Sample` site, 21 files, peaks at about 45 MB.

## In a container

When memory is scarce, lower `-p` first:

```dockerfile
RUN douze -s site -o /out -p 2
```

- A build that runs out of memory fails with an error, and keeps the cache of the previous build.
- Lowering `-p` is the way to use less memory: a heap limit set in the environment (`DOTNET_GCHeapHardLimit`) does not
  cap `douze`.
- `--no-cache` saves the cache's disk space, not memory.

## See also

- [Command line](../reference/cli.md)
- [Configuration reference](../reference/configuration.md)
- [Benchmarks](benchmarks.md)
- [Progress and tracing](tracing.md)
- [Build your site with Docker](../recipes/docker.md)
