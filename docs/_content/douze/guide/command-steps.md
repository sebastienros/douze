---
title: Command steps
description: Run external tools as steps of the workflow, one process per file, one for all the files, or a long-running worker.
liquid: false
---

A command step runs an external program as a step of the workflow: a minifier, a search indexer, an image generator,
a Node script. It has `run` instead of `uses`, receives files like any step, and its output files continue through
the workflow. Douze caches its results like those of built-in steps, so an unchanged file does not run the command
again.

```yaml title="douze.yml"
minify:
  needs: layout
  run: "npx html-minifier-terser --collapse-whitespace"
  mode: filter
  match: "**/*.html"
```

## Modes

`mode` says how files reach the program:

| Mode | Processes | Shape | Use it for |
|---|---|---|---|
| `filter` | one per file | map: content on stdin, new content on stdout | a few files, or tools that are not Node |
| `batch` (default) | one for all the files, or one per group with `group-by` | reduce: files are written to a folder | tools that want a folder (Pagefind, a CSS bundler, OG images) |
| `worker` | one for the step, sent each file as JSON lines | map | per-file work at scale |

Starting Node takes about 50 ms, so a `filter` over thousands of files spends minutes starting processes. A worker
starts once, while the steps before it run.

## Filter: one process per file

The file's content goes to stdin, and stdout becomes its new content. The path and data do not change.

```yaml title="douze.yml"
  upper:
    needs: markdown
    run: "tr a-z A-Z"
    mode: filter
    match: "**/*.html"
```

The process also gets `DOUZE_PATH`, `DOUZE_CONTENT_TYPE` and `DOUZE_METADATA` (the file's data as JSON). Files run in
parallel, up to `-p` at once. In the `run` line, `{{ file.path }}`, `{{ file.dir }}`, `{{ file.name }}` and
`{{ file.ext }}` describe the file.

## Batch: one process for a folder of files

When the `run` line references `{{ inputs }}`, every file the step consumes is written under that folder at its path,
next to a `douze-inputs.json` that lists them with their data. When it references `{{ outputs }}`, that folder starts
empty, and every file the command writes there becomes a file of the site at the same path: `<outputs>/css/site.css`
is `/css/site.css`.

The consumed files are replaced by the outputs. Use `match` so the step consumes only the files it needs; the others
pass through.

This step bundles and minifies the stylesheets with Lightning CSS. The step consumes every `.css` file and emits one
bundle, so the imported files are not published on their own:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  css:
    needs: permalink
    match: "css/**/*.css"
    tool: lightningcss
    run: "{{ tool }} --minify --bundle {{ inputs }}/css/site.css -o {{ outputs }}/css/site.css"
  render:
    needs: css
    uses: liquid
  write:
    needs: render
    uses: write
```

The step sits in the chain (`render` needs `css`), rather than on a branch next to it: on a branch, `write` would
receive `/css/site.css` from both the branch and the chain, which is an error.

A command that references neither folder gets no files and emits none: it runs for its side effects, after the steps
it needs.

## Worker: one process, many files

A worker is a long-running process that reads one JSON request per line on stdin and answers on stdout.
`douze worker-helper` writes a small Node module that speaks the protocol:

```sh
douze worker-helper > scripts/douze-worker.mjs
```

```js title="scripts/stamp.mjs"
import { serve } from './douze-worker.mjs';

serve(async (file, ctx) => {
  return { content: file.content.replace('</h1>', ` <small>${ctx.options.label}</small></h1>`) };
}, { concurrency: 4, name: 'stamp' });
```

```yaml title="douze.yml"
stamp:
  needs: markdown
  run: "node scripts/stamp.mjs"
  mode: worker
  match: "**/*.html"
  sources: ["scripts/*.mjs"]
  with:
    label: draft
```

`with` is sent to the worker as `options`. The handler returns new content, a file with a new path, content type or
data, several files, or `null` to drop the file. See [Write a Node worker step](../recipes/node-worker.md) and the
[protocol](../reference/commands.md#worker).

## Tools that Douze downloads

`tool` runs a native tool that Douze downloads on first use, so a site needs no package manager for it. `{{ tool }}`
is the path of its binary:

| `tool` | Program | Default version |
|---|---|---|
| `pagefind` | Pagefind's extended binary | 1.5.2 |
| `lightningcss` | The Lightning CSS command line | 1.33.0 |

The packages come from npm for the platform, are verified against the integrity Douze pins for the default version,
and are installed into the cache folder (`.douze/tools/`). `pagefind@1.4.0` picks another version, verified
against the integrity the npm registry gives. `douze tools install` downloads them ahead of time, for CI caches or offline machines.

This step indexes the pages with Pagefind. It consumes the HTML pages and emits the index, and `write` needs both
`render` (for the pages) and `search` (for the index):

```yaml title="douze.yml"
search:
  needs: render
  match: "**/*.html"
  tool: pagefind
  run: "{{ tool }} --site {{ inputs }} --output-path {{ outputs }}/pagefind"
write:
  needs: [render, search]
  uses: write
```

Since the index is a set of output files, Douze writes it, caches it and deletes its stale files like any other file.
See [Add search with Pagefind](../recipes/pagefind.md).

## The `run` line

`run` is a Liquid template, rendered with `{{ step }}`, `{{ site }}` (the output folder), `{{ cwd }}`,
`{{ inputs }}` and `{{ outputs }}` (batch), `{{ file.* }}` (filter) and `{{ tool }}`. A value written directly is
quoted for the shell when it needs it (a path with spaces); the result of a filter is written as it is.

- `shell` (default `true`) runs the line through `/bin/sh -c`, or `cmd.exe /d /s /c` on Windows. `shell: false`
  splits the line and runs the program directly. On Windows, use the shell for `npx` and other `.cmd` scripts.
- `cwd` is the working directory, relative to the source folder (default: the source folder).
- `env` adds environment variables. See [Environment variables](environment-variables.md#variables-of-command-steps).
- `timeout` (`30s`, `5m`) kills the process tree and fails the unit; for a worker, it applies to each file.

Before the build starts, the program the line starts with is looked up on the `PATH`, or relative to `cwd` when it is
a path. A missing program fails the build at once, with what to install:

```txt
error: minify: esbuild is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'
```

`douze doctor` runs the same checks without building.

## Errors and stderr

A non-zero exit code fails the unit, with the end of stderr:

```txt
error: minify: /blog/a.html: command exited with code 2 for '/blog/a.html': <last 20 lines of stderr>
```

`stderr` says what output on stderr means when the exit code is 0: `fail` (the default), `warn` (shown as a warning)
or `ignore`. Node prints some warnings on stderr (`ExperimentalWarning`): set `NODE_NO_WARNINGS: "1"` in `env`, or
`stderr: warn`.

## Linters and validators

A linter fails the build the same way: a non-zero exit code or output on stderr. Run it on a branch, a step that no
other step needs, so that the files it consumes do not replace the files that continue to `write`:

```yaml title="douze.yml"
lint-css:
  needs: pages
  match: "**/*.css"
  run: "npx stylelint \"{{ inputs }}/**/*.css\""
validate-html:
  needs: layout
  match: "**/*.html"
  run: "npx html-validate {{ inputs }}"
```

For the syntax of code samples (YAML, JSON, XML, Liquid, `douze.yml`) and of generated feeds, the built-in
[check-code](../reference/steps/check-code.md) step needs no tool.

## Caching

A command's result is cached by the files it received, its fields, its `version`, and the content of the files its
`sources` globs match. Add the scripts a command runs to `sources`, so that editing them runs the step again, and
bump `version` when the program itself changes (a new release of a tool on the `PATH`).

A command that writes into the output folder itself (`{{ site }}`) is invisible to the cache: when its inputs do not
change it does not run, even if its files were deleted. Prefer `{{ outputs }}`, or set `incremental: false` to run
the step on every build.

## See also

- [Command steps and workers](../reference/commands.md)
- [Environment variables](environment-variables.md)
- [Workflow architecture](workflow-architecture.md)
- [Minify HTML with a command step](../recipes/minify-html.md)
- [check-code step reference](../reference/steps/check-code.md)
- [Write a Node worker step](../recipes/node-worker.md)
- [Add search with Pagefind](../recipes/pagefind.md)
