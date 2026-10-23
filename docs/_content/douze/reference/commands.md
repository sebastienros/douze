---
title: Command steps and workers
description: The fields, modes, template values, environment and worker protocol of steps that run external commands.
liquid: false
---

A step with `run` instead of `uses` runs an external command, such as a Node script, a minifier or a search indexer.
Its `mode` says how files reach the command. This page is the reference; [Command steps](../guide/command-steps.md)
explains when to use each mode.

```yaml title="douze.yml"
steps:
  minify:
    needs: layout
    run: "npx html-minifier-terser --collapse-whitespace"
    mode: filter
    match: "**/*.html"
  search:
    needs: write
    tool: pagefind
    run: "{{ tool }} --site {{ site }}"
  og:
    needs: layout
    run: "node scripts/og.mjs {{ inputs }} {{ outputs }}"
    match: "blog/**/*.html"
    outputs: "**/*.png"
  hover:
    needs: markdown
    run: "node scripts/worker.mjs"
    mode: worker
    match: "**/*.html"
    sources: "scripts/*.mjs"
```

## Modes

| Mode | Processes | Shape | Files |
|---|---|---|---|
| `filter` | One per file | map | The content goes to stdin, stdout is the new content. |
| `batch` (default) | One for all the files, or one per group with `group-by` | reduce, or group | The files are written to a folder, the command writes its outputs to another. |
| `worker` | One for the step | map | Each file is sent to the process as a JSON line. |

Starting Node takes about 50 ms, so a `filter` over thousands of files spends most of its time starting processes; a
worker starts once.

## Fields

| Field | Type | Default | Modes | Meaning |
|---|---|---|---|---|
| `run` | string | | all | The command line, a Liquid template (see [template values](#template-values)). |
| `mode` | `filter`, `batch`, `worker` | `batch` | | How files reach the command. |
| `tool` | `name` or `name@version` | | all | A tool Douze downloads, whose binary is `{{ tool }}`: see [tool](#tool). |
| `match` | glob or list | every file | all | The entering files the command consumes; the others pass through. |
| `env` | mapping of names to strings | | all | Environment variables. Values are templates, never quoted. |
| `cwd` | folder | the source folder | all | The working directory, relative to the source folder. It must exist. |
| `timeout` | duration | none | all | `30s`, `5m`: the process tree is killed and the unit fails. For a worker, each request has this long. |
| `stderr` | `fail`, `warn`, `ignore` | `fail` | all | What output on stderr means: a failure, a warning, or nothing. |
| `shell` | `true` or `false` | `true` | all | Run through `/bin/sh -c` (`cmd.exe /d /s /c` on Windows), or split the line and start the program directly. |
| `sources` | glob or list | none | all | Files of the source folder (the scripts the command runs, data it reads) whose paths and contents are part of the cache key. |
| `version` | string | empty | all | Part of the cache key: change it to run the command again. |
| `outputs` | glob or list | every file | `batch` | The files read back from the outputs folder. |
| `group-by` | `file`, `dir` or a template | none | `batch` | One process per group. |
| `with` | mapping | none | `worker` | Options sent to the worker in `hello`. |
| `incremental` | `true` or `false` | `true` | all | `false` runs the command on every build. |

A command reads no site data and no other scopes: its results depend on its definition, the files it consumes, its
`version` and its `sources`. Errors: `'with' is only valid with 'uses' or with 'mode: worker'`,
`'outputs' is only valid with 'mode: batch'`, `'<field>' is only valid with 'run'` on a `uses` step.

### Exit code and stderr

A non-zero exit code fails the unit with the end of stderr (its last 20 lines):

```text
error: minify: /blog/a.html: command exited with code 2 for '/blog/a.html': <end of stderr>
```

Output on stderr fails the unit with `stderr: fail` (`command wrote to stderr for '/blog/a.html': ...`), is a warning
with `stderr: warn`, and is ignored with `stderr: ignore`. Node prints some warnings on stderr (`ExperimentalWarning`):
set `NODE_NO_WARNINGS: "1"` in `env`, or use `stderr: warn`.

### Requirements

Before any unit runs, the program the `run` line starts with is looked up on `PATH` (with `PATHEXT` on Windows), or
relative to `cwd` when it is a path (`./scripts/build.sh`). Shell commands (`echo`, `cd`) need nothing, and a line that
starts with a template or a variable is `unchecked`. A missing program fails the build at once:

```text
error: minify: esbuild is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'
```

`douze doctor` lists these checks without building, see [Command line](cli.md#doctor).

## Template values

`run` and the `env` values are Liquid templates rendered with:

| Value | Modes | Meaning |
|---|---|---|
| `{{ step }}` | all | The step name. |
| `{{ site }}` | all | The output folder, absolute. |
| `{{ cwd }}` | all | The working directory, absolute. |
| `{{ tool }}` | all | The binary of the step's `tool`. |
| `{{ inputs }}` | `batch` | The folder holding the input files. |
| `{{ outputs }}` | `batch` | The folder the command writes its files to. |
| `{{ file.path }}` | `filter` | The file's virtual path: `/blog/a.md`. |
| `{{ file.dir }}` | `filter` | Its folder: `/blog/`. |
| `{{ file.name }}` | `filter` | Its name: `a.md`. |
| `{{ file.ext }}` | `filter` | Its extension: `.md`. |

In `run`, a value written directly is quoted for the shell when it holds anything but letters, digits and
`_-./:@+=,`: `'/my site/_site'` with `/bin/sh`, `"C:\my site\_site"` with `cmd.exe`. The result of a filter
(`{{ file.name | remove: '.md' }}`) is written as the filter returns it. `env` values are never quoted.

Errors: `'{{ inputs }}' is only valid with 'mode: batch' (step 'x')` (also `outputs`),
`'{{ file }}' is only valid with 'mode: filter' (step 'x')`, and
`invalid template in 'run' of step 'x': End of tag '}}' was expected at (1:5)`.

With `shell: false`, the line is split on whitespace, `'...'` is literal, and in `"..."` a `\"` is a quote. On Windows,
use the shell for `npx` and other `.cmd` scripts.

## Environment

Every command gets the step's `env`, then:

| Variable | Modes | Value |
|---|---|---|
| `DOUZE_STEP` | all | The step name. |
| `DOUZE_SITE` | all | The output folder, absolute. |
| `DOUZE_SOURCE` | all | The source folder, absolute. |
| `DOUZE_PATH` | `filter` | The file's virtual path: `/blog/a.md`. |
| `DOUZE_CONTENT_TYPE` | `filter` | The file's content type: `text/html`. |
| `DOUZE_METADATA` | `filter` | The file's own data as JSON, keys sorted. |
| `DOUZE_METADATA_FILE` | `filter` | Instead of `DOUZE_METADATA` when the JSON is 32 KB or more: the path of a file holding it. |

## filter

```yaml title="douze.yml"
steps:
  minify:
    needs: layout
    run: "npx html-minifier-terser --collapse-whitespace"
    mode: filter
    match: "**/*.html"
```

One process per consumed file, up to `-p` at once. The file's content goes to stdin, stdout becomes its new content;
the path and data do not change.

## batch

```yaml title="douze.yml"
steps:
  sizes:
    needs: markdown
    run: "./scripts/sizes.sh {{ inputs }} {{ outputs }}"
    match: "**/*.html"
    outputs: "**/*.txt"
  write:
    needs: [markdown, sizes]
    uses: write
```

One process for every file the step consumes, or one per group with `group-by`.

- When `run` or `env` references `{{ inputs }}`, every consumed file is written under that folder at its virtual path,
  next to `douze-inputs.json`.
- When it references `{{ outputs }}`, that folder starts empty, and once the command succeeds every file it wrote
  there that matches `outputs` becomes a file of the site at the same path: `out/blog/a.png` is `/blog/a.png`. An
  optional `douze-outputs.json` gives files a content type and data.
- The consumed files are replaced by the outputs. A step that must keep them consumes only what it needs (`match`), and
  the steps after it need the step before it too, as `write` does above.
- A command that references neither (`pagefind --site {{ site }}`) gets nothing on disk and emits no files.
- stdout is shown with `--verbose`.

The folders are `<cache>/tmp/<build>/<step>/<unit>/in` and `out` (the cache folder being `<source>/.douze` or
`--cache-dir`), and are deleted after the run unless `--verbose`.

### douze-inputs.json

```json
{
  "step": "og",
  "unit": "*",
  "files": [
    { "path": "/blog/a.html", "contentType": "text/html", "metadata": { "title": "A", "url": "/blog/a/index.html" } }
  ]
}
```

`unit` is `*` without `group-by`, the group key with it. `metadata` is the file's own data.

### douze-outputs.json

The same shape, written by the command into the outputs folder. Each entry needs a `path` of a file of the outputs
folder, and can give its `contentType` and `metadata`. A file without an entry gets the content type of its extension.
Errors: `douze-outputs.json is not valid JSON: ...`, `douze-outputs.json needs a 'files' array`,
`every entry of douze-outputs.json needs a 'path'`, and
`douze-outputs.json lists '<path>', which is not in the outputs folder`.

## worker

```yaml title="douze.yml"
steps:
  upper:
    needs: markdown
    run: "node scripts/upper.mjs"
    mode: worker
    match: "**/*.html"
    sources: "scripts/*.mjs"
    with:
      greeting: hi
```

A worker is a long-running process that reads requests on stdin and writes answers on stdout, one JSON object per
line (UTF-8, `\n` terminated). Without the cache, it starts with the step, while the steps before it run; with the
cache, it starts when the first unit runs, so a build whose units all come from the cache starts no process. It stops
when the step is done.

### Protocol

The protocol version is 1.

1. Handshake. Douze sends `hello`; the worker answers `ready` within 30 seconds. `options` is the step's `with`,
   `concurrency` the build's parallelism. The worker answers with how many requests it takes at once (1 when omitted);
   Douze sends at most the smaller of both.

   ```json
   {"type":"hello","protocol":1,"step":"upper","concurrency":8,"options":{"greeting":"hi"},"site":"/abs/_site","source":"/abs/src"}
   {"type":"ready","protocol":1,"concurrency":4,"name":"upper"}
   ```

2. Requests. One `file` per consumed file, answered in any order with a `result` or an `error` of the same `id`.
   `encoding` is `utf8` for text and `base64` for binary content.

   ```json
   {"type":"file","id":"42","path":"/docs/a.html","contentType":"text/html","encoding":"utf8","content":"<h1>A</h1>","metadata":{"title":"A"}}
   {"type":"result","id":"42","files":[{"path":"/docs/a.html","encoding":"utf8","content":"<H1>A</H1>","metadata":{"upper":true}}]}
   {"type":"error","id":"42","message":"TS2304: Cannot find name 'foo'","file":"/docs/a.md","line":12,"column":3}
   ```

   A `result` has zero files (the file is dropped), one or many. In a file, an omitted `path` keeps the request's, an
   omitted `content` keeps its content, and an omitted `contentType` keeps its type when the path is unchanged, or
   takes the type of the new path's extension. `metadata` is merged over the file's data (top-level keys). An `error`
   fails that file only; `message`, `file`, `line` and `column` are shown.

3. Logs, at any time, shown with the step name. `level` is `debug`, `info`, `warn` or `error` (default `info`):

   ```json
   {"type":"log","level":"info","message":"loaded api.d.ts in 1.2 s"}
   ```

4. Shutdown. After the last answer, Douze sends `shutdown` and closes stdin. The worker exits with code 0 within 10
   seconds, or it is killed.

   ```json
   {"type":"shutdown"}
   ```

Unknown message types are ignored. A line that is not JSON, output on stderr with `stderr: fail`, a request that times
out, or a worker that exits before `shutdown` fails every request in flight and the later ones, and kills the process
tree: `worker exited with code 1: <end of stderr>`. Other messages: `worker wrote a line that is not JSON: ...`,
`worker closed its input`, `worker exited with code <n> after 'shutdown'`,
`worker returned '<path>', which is not a file path, for '<path>'`.

### The worker helper

`douze worker-helper` prints `douze-worker.mjs`, a small Node module that speaks the protocol:

```bash
douze worker-helper > scripts/douze-worker.mjs
```

```js title="scripts/upper.mjs"
import { serve } from './douze-worker.mjs';

serve(async (file, ctx) => {
  ctx.log(`upper ${file.path}`, 'debug');
  return { content: file.content.toUpperCase(), metadata: { upper: true } };
}, { concurrency: 4, name: 'upper' });
```

`serve(handler, { concurrency = 1, name = 'worker' })` answers the handshake with `concurrency` and `name`, then calls
the handler for each request:

| Parameter | Members |
|---|---|
| `file` | `path`, `contentType`, `metadata`, `content`: a string for text, a `Buffer` otherwise. |
| `ctx` | `step`, `options` (the step's `with`), `site`, `source`, and `log(message, level = 'info')`. |

The handler returns:

- `null` or `undefined`: the file is dropped;
- a string or a `Buffer`: the new content;
- an object `{ content?, path?, contentType?, metadata? }`: what is omitted keeps the input's value, and `metadata` is
  merged over the input's;
- an array of those: several files.

A thrown error fails that file only; an error with `line` and `column` properties gives Douze a position. See
[Write a Node worker step](../recipes/node-worker.md).

## tool

```yaml title="douze.yml"
steps:
  search:
    needs: write
    tool: pagefind
    run: "{{ tool }} --site {{ site }}"
```

`tool` runs a native tool that Douze downloads, so that a site needs no package manager for it. `{{ tool }}` is the
path of its binary.

| Tool | Default version | Package |
|---|---|---|
| `pagefind` | 1.5.2 | Pagefind's extended binary, `@pagefind/<os>-<arch>` |
| `lightningcss` | 1.33.0 | The Lightning CSS command line, `lightningcss-cli-<os>-<arch>` |

`pagefind@<version>` picks another version. The package of the platform is downloaded from the npm registry on first use,
verified against the integrity Douze pins for the default version (or the one the registry gives for another), into
`<cache>/tools/`. `douze tools install` downloads it ahead, and `douze doctor` lists it as `download` until then. An
unknown tool is an error: `unknown tool 'nope' for step 'x'; Douze knows lightningcss, pagefind`.

## Caching

A command's result is reused when its definition, its input files, its `version` and the files its `sources` match did
not change. Without `sources`, editing the script a command runs does not run it again: list the script in `sources`,
or bump `version`. `incremental: false` runs the step on every build. See
[Cache and incremental builds](../guide/cache.md).
