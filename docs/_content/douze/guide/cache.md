---
title: Cache and incremental builds
description: How the disk cache makes a second build run only what changed, what invalidates a result, stale output cleanup, and caching in CI.
---

Douze keeps the result of every unit of work in a disk cache, and the next build reuses every result whose inputs did
not change. A second build without changes runs nothing, and after an edit, only what the edit affects runs. There is
no separate incremental mode: every `douze build`, `douze watch` and `douze serve` uses the cache. This page explains
what is cached, what makes a result stale, how Douze cleans the output folder, and how to keep the cache in CI.

## What a build reuses

A unit is a file in a step, a group of files, or a whole step for the steps that need every page (`collect`, `data`).
The build prints how many units ran and how many came from the cache:

```
Build succeeded: 9 steps, 4 files written in 0.02 s, 0 units executed, 29 cached
```

After editing the body of one page:

```
Build succeeded: 9 steps, 4 files written in 0.02 s, 9 units executed, 20 cached
```

The cache is in `<source>/.douze/cache/`. Add `.douze/` to your `.gitignore`. The folder also holds `tools/`, the
tools that steps download (language servers, Pagefind), and `tmp/`, the working folders of command steps.

## What a result depends on

A unit runs again when one of these changed:

- the files it received: their content and their data;
- the step's options, and the `markdown` section for the steps that render Markdown;
- the layouts, includes, shortcodes and templates it read;
- the data it read: folder data, site data, collections, `site` settings. Only the values it read count, so editing
  one key of `_data/site.json` renders again the pages that read that key;
- for a command step, its `version` and the files its `sources` match;
- the version of Douze: a cache written by another version is discarded.

Data flows down the workflow, so a change runs the steps after it only as far as the result differs. Editing the body
of a post renders that post again, but not the index that lists it, since collections hold the title, tags and date of
pages and not their content. Changing its title renders the index too.

Some steps cache finer results inside their unit. `highlight` keeps the tokens of each fence by language and code, and
the hover steps keep the outcome of each snippet. A changed page highlights and checks only its new fences.

`incremental: false` on a step runs it on every build. Use it for a command that reads something Douze does not see,
such as a remote API.

### File dates

The front matter step sets `page.date` to the file's modification time, so touching a file changes its data, even when
its content is the same: the page runs again, and so does everything that reads its date. A fresh `git clone` gives
every file a new date, which runs every page again. The fence results of `highlight` and the hover steps are keyed by
code, not by page, so they still come from the cache.

## The write step

`write` writes a file again when it is missing from the output folder, or when its content there changed since Douze
wrote it. Deleting the output folder is safe: the next build writes everything again from the cache, and only the `write`
units run.

## Stale outputs

Once a build succeeds, it deletes the output files that previous builds wrote and this one did not: the pages of
deleted or renamed sources, the pages that a pagination no longer has, and the folders they leave empty. A file that
something else changed since Douze wrote it stays. Nothing is deleted when the build fails, or with `--no-cache`, which
has no record of previous builds.

## Options

| Option | Effect |
|---|---|
| `--no-cache` | builds without reading or writing the cache |
| `--cache-dir <dir>` | moves the cache folder (default `<source>/.douze`), tools included |

Only one build uses a cache at a time. A build that finds the cache in use, during a `douze serve` session for
instance, builds without it and prints a warning. The session writes the cache's index when it ends.

A successful build drops the results it did not use, so the cache does not grow with every edit. A build that fails
or is cancelled keeps the previous results.

## Cache in CI

Restoring the cache folder between CI runs saves the downloads of tools and the work of the steps that cache by
content: highlighting and code checks. Pages still render again after a fresh checkout, because the files' dates
changed (see [File dates](#file-dates)).

```yaml title=".github/workflows/site.yml"
      - uses: actions/cache@v4
        with:
          path: .cache/douze
          key: douze-${{ runner.os }}-${{ github.sha }}
          restore-keys: douze-${{ runner.os }}-
      - run: douze tools install --cache-dir .cache/douze
      - run: douze --cache-dir .cache/douze
```

The key changes on every commit so that each run saves its cache, and `restore-keys` restores the latest one.
`douze tools install` downloads the tools ahead, so that a step's first build does not, and the cache has them.

## See also

- [Command line](../reference/cli.md)
- [Develop and build](develop-and-build.md)
- [Progress and tracing](tracing.md)
- [Memory and parallelism](memory-and-parallelism.md)
- [Code hovers](code-hovers.md)
