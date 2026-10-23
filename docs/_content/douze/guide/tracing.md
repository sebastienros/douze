---
title: Progress and tracing
description: The live view, the plain view and the summary of a build, and the trace and timeline files that show where its time goes.
---

Douze shows what a build is doing while it runs, and a summary of every step when it ends. When a build is slow,
`--timeline` writes a page with the build on a time axis, and `--trace` writes a trace for Perfetto. This page covers
the views, the trace files, and how to read a timeline to find the step that holds the others back.

## The live view

In a terminal, Douze shows a table of the running steps: the shape of each step, the units done out of the total
(`done/?` while a streaming step does not know its total yet), a progress bar and the elapsed time. Each step that
completes prints one line above the table. At the end, a summary lists every step:

```
  Step          State       In   Out   Units   Cached     Time   0   0.01 s
  ──────────────────────────────────────────────────────────────────────────
  folder-data   succeeded    1     0       1        0   0.00 s   ▎
  pages         succeeded    4     4       4        0   0.00 s   ▍
  permalink     succeeded    4     4       4        0   0.00 s    ▌
  collections   succeeded    4     0       1        0   0.01 s    ███████▋
  paginate      succeeded    4     4       4        0   0.00 s           ▏
  render        succeeded    4     4       4        0   0.00 s           █▋
  markdown      succeeded    4     3       3        0   0.00 s           ▊
  layout        succeeded    4     4       4        0   0.00 s            ▎
  write         succeeded    4     4       4        0   0.00 s            █

Build succeeded: 9 steps, 4 files written in 0.02 s, 29 units executed
```

The last column places each step on the time scale of the build, so you can see which steps ran at the same time.
A step's time starts when its first file reaches it, not while it waits for the steps it needs.

## The plain view

The live view is replaced by one line per step:

- with `--plain`;
- when the output is redirected to a file or a pipe;
- when `CI` is set, to anything but `false` or `0`;
- when `TERM=dumb`.

```
✓ pages            4 files    0.00 s
✓ render           4 files    0.00 s       1 cached
Build succeeded: 9 steps, 4 files written in 0.02 s, 1 unit executed, 28 cached
```

`NO_COLOR` keeps the live view without colors. `-q` (`--quiet`) prints errors only. `-v` (`--verbose`) adds stack
traces to errors, and in the plain view a line per unit, the output of batch commands, and keeps their working
folders.

## Timeline

```sh
douze --timeline build.html
douze --trace-units --timeline build.html
```

`--timeline` writes one HTML file that works offline: a row per step on a time axis, red when the step failed, with the
spans of its commands packed under it. `--trace-units` adds a span per file or group. Hover a span for its details,
zoom with the mouse wheel, pan by dragging. A table lists the spans, sortable by duration. The page follows the light
or dark theme of the system.

Unit spans are off by default because a site has thousands of units of a few microseconds each, which are noise.
Turn them on to find the slow pages of a step.

## Trace

```sh
douze --trace build.json
```

`--trace` writes [Chrome trace events](https://docs.google.com/document/d/1CvAClvFfyA5R-PhYUmn5OOQtYMH4h6I0nSsKchNAySU)
that open in [Perfetto](https://ui.perfetto.dev) or `chrome://tracing`: a lane for the build, then lanes for the
steps, then for units, commands and I/O, so that overlapping spans never share a lane. Times are in microseconds since
the start of the build. Both options can be used together. Both files are written even when the build fails or is
cancelled.

## Find a slow step

1. Build with `--no-cache` so that every unit runs, with `--trace-units`:

   ```sh
   douze --no-cache --trace-units --timeline build.html
   ```

2. Open `build.html`. Look for the step whose row is long and that starts the rows below it when it ends. That step
   is on the critical path: the steps that need it wait for it.
3. A barrier step, such as `collect`, or a batch command step, needs every page before it can run, so the steps
   after it wait for the slowest page upstream. A long wait there usually points at a step before it.
4. Sort the table by duration to find the slowest units, often a page with many code fences or a large image.
5. A command step's spans show the time of its processes. A `filter` step spends about 50 ms per file starting Node:
   a `worker` step starts one process for all the files (see [Command steps](command-steps.md)).

Then try again with the cache to see what an edit costs: `douze --trace-units --timeline build.html` after changing
one page shows the units that ran for it.

## See also

- [Command line](../reference/cli.md)
- [Cache and incremental builds](cache.md)
- [Memory and parallelism](memory-and-parallelism.md)
- [Benchmarks](benchmarks.md)
- [Troubleshooting](troubleshooting.md)
