# douze
Stupidly simple, blazing fast static site generator

# installation

```
dotnet tool install -g douze --prerelease
```

# usage

```
douze [build] [options]          build once (default command)

Options:
  -s, --source <dir>             source folder (default: .)
  -o, --output <dir>             output folder (default: _site)
  -p, --parallelism <n>          max parallel units (default: processor count)
      --keep-going               report every error instead of stopping the failing step
      --fail-fast                stop everything on the first error
      --plain                    one line per step, no live view
  -q, --quiet                    errors only
  -v, --verbose                  one line per file, stack traces
      --print-workflow           print the effective workflow as YAML and exit
  -h, --help                     show the help
      --version                  show the version
```

`douze --help` lists the options of features that are not available yet (workflow files, watch mode, dev server,
trace files, cache).

The exit code is 0 on success, 1 when the build has errors (each one is printed as
`error: <step>: <file>(<line>,<col>): <message>`), 2 for an invalid command line or workflow, and 130 when the
build is cancelled with Ctrl+C.

# site layout

| Folder | Default | Contains |
|---|---|---|
| content | `_content` if it exists, else the source folder | pages, data files that apply to a folder, and any other file to publish |
| data | `_data` if it exists, else none | site data |
| includes | `_includes` if it exists, else the source folder | layouts, includes and templates |

The output folder is skipped when it is inside the source folder.

# how a site is built

Douze runs a workflow: a graph of steps that files flow through. `douze --print-workflow` prints it. The default
workflow:

1. Reads the data files (`.yml`, `.yaml`, `.json`, `.xml`) of the data folder as site data, and the ones of the
   content folder as folder data.
2. Moves the YAML front matter of `.md`, `.html` and `.liquid` files into their data, and sets `page.date` to the
   file's modification time.
3. Renders Markdown to HTML (`post.md` becomes `post.html`).
4. Expands paginated pages.
5. Computes output paths: `blog/post.html` is written to `blog/post/index.html`, `index.html` stays in place,
   `permalink` overrides the path, and `permalink: false` skips the file. The content folder is removed from the
   path, and `url` holds the result (`/blog/post/index.html`).
6. Builds `collections.all` and `collections.<tag>` from the `tags` of every page.
7. Renders every HTML and JSON file as a Liquid template.
8. Applies layouts.
9. Writes every file to the output folder. Files that no step claims (images, stylesheets, scripts) are copied as
   they are.

## data

Each file of the data folder is a site value named after the file: `_data/colors.json` is `colors`, whatever it
contains (a list, an object or a single value), and `_data/pkgs/Aspire.Hosting.json` is `pkgs["Aspire.Hosting"]`.
Two files cannot define the same name.

The top-level keys of the data files of a content folder (`articles/articles.yml`) apply to the pages of that folder
and its subfolders.

A name in a template resolves to the first of: the page's own data (front matter, `page.date`, `url`, captured
variables), the data of its folder, then of the parent folders, the site data, and the front matter of its layouts.
For instance a page can set `layout`, or inherit it from its folder or from `_data/layout.yml`.

## pagination

```yaml
---
pagination:
  data: products     # a name, or a dotted path into one such as site.products
  size: 2
  alias: items       # optional: the items of the page
  resolve: values    # optional, for objects: paginate the values instead of the keys
products: [a, b, c]
---
```

Each page has `pagination.items`, `pagination.pageNumber`, `pagination.hrefs` (the path of every page),
`pagination.pages` and `pagination.page.next`, `previous`, `first`, `last` (each one is `items`, `pageNumber` and
`href`). A `permalink` is rendered for each page, so it can use the alias or `pagination.pageNumber` (not the
`href` values, which come from it); without one, pages are written to `products/index.html`,
`products/1/index.html`... Pagination runs before collections are built, so `collections.<tag>` cannot be paginated
by the default workflow.

## layouts

`layout` names a file of the includes folder. The layout is rendered with the page's data and `content`, and can
itself have a `layout` in its front matter.

# workflow

Not available yet: `douze.yml` describes a site's own workflow (`site` settings and `steps`).

# commands

A step with `run` instead of `uses` runs an external tool, such as a Node script. `mode` says how files reach it:

| Mode | Processes | Shape | Use it for |
|---|---|---|---|
| `filter` | one per file | map: content in on stdin, out on stdout | a handful of files, or tools that are not Node |
| `batch` (default) | one for all the files, or one per group with `group-by` | reduce: the files are written to a folder | tools that want a folder (OG images, Pagefind) |
| `worker` | one for the step, sent each file over JSON lines | map | per-file work at scale (twoslash on thousands of fences) |

Starting Node costs about 50 ms, so a `filter` over thousands of files spends minutes starting processes; a worker
starts once, while the steps before it run.

```yaml
minify: { needs: layout, run: "npx html-minifier-terser --collapse-whitespace", mode: filter, match: "**/*.html" }
search: { needs: write, run: "npx pagefind --site {{ site }}" }
og-images: { needs: render, run: "node scripts/og.mjs {{ inputs }} {{ outputs }}", match: "blog/**/*.html", outputs: "**/*.png" }
twoslash: { needs: render, run: "node scripts/twoslash-worker.mjs", mode: worker, match: "**/*.md", sources: ["scripts/twoslash-worker.mjs"] }
```

The fields of a command step:

| Field | Default | Meaning |
|---|---|---|
| `run` | | The command line, a Liquid template (below) |
| `mode` | `batch` | `filter`, `batch` or `worker` |
| `env` | | Environment variables; values are templates too |
| `cwd` | the source folder | Working directory, relative to the source folder |
| `timeout` | none | `30s`, `5m`: the process tree is killed and the unit fails. For a worker, each file has this long |
| `stderr` | `fail` | What output on stderr means: `fail`, `warn` (shown as a warning) or `ignore` |
| `shell` | `true` | Run through `/bin/sh -c` (`cmd.exe /d /s /c` on Windows); `false` splits the line and runs the program directly |
| `outputs` | every file | Batch: globs of the files read back from the outputs folder |
| `group-by` | | Batch: `file`, `dir` or a Liquid template; one process per group |
| `with` | | Worker: options sent to the worker in `hello` |

A non-zero exit code fails the unit with the end of stderr:
`error: minify: /blog/a.html: command exited with code 2 for '/blog/a.html': <last 20 lines of stderr>`. Node prints
some warnings on stderr (`ExperimentalWarning`): set `NODE_NO_WARNINGS: "1"` in `env`, or `stderr: warn`.

## the command line

`run` and `env` are rendered with these values:

| Value | Modes | Meaning |
|---|---|---|
| `{{ step }}` | all | The step name |
| `{{ site }}` | all | The output folder (absolute) |
| `{{ cwd }}` | all | The working directory (absolute) |
| `{{ inputs }}` | batch | The folder holding the input files |
| `{{ outputs }}` | batch | The folder the command writes its files to |
| `{{ file.path }}`, `file.dir`, `file.name`, `file.ext` | filter | The file's virtual path (`/blog/a.md`), folder (`/blog/`), name (`a.md`), extension (`.md`) |

A value written directly in `run` is quoted for the shell when it needs it (`'/my site/_site'` with `/bin/sh`,
`"C:\my site\_site"` with `cmd.exe`); the result of a filter (`{{ file.name | remove: '.md' }}`) is written as it is.
`env` values are never quoted. The environment also has `DOUZE_STEP`, `DOUZE_SITE` (output folder) and
`DOUZE_SOURCE` (source folder).

With `shell: false`, the line is split on whitespace, `'...'` is literal, and in `"..."` a `\"` is a quote. The
program is looked up on the PATH; on Windows, use the shell for `npx` and other `.cmd` scripts.

## filter

The file's content goes to stdin, stdout becomes its new content; the path and data are unchanged. The command also
gets `DOUZE_PATH` (`/blog/a.md`), `DOUZE_CONTENT_TYPE` and `DOUZE_METADATA` (the file's own data as JSON), or
`DOUZE_METADATA_FILE` (a file holding it) when the JSON is over 32 KB. Files run in parallel, up to `-p` at once.

## batch

When the command line references `{{ inputs }}`, every file the step consumes is written under that folder at its
path, next to `douze-inputs.json`:

```json
{ "step": "og-images", "unit": "*", "files": [
  { "path": "/blog/a.html", "contentType": "text/html", "metadata": { "title": "A", "url": "/blog/a/" } } ] }
```

When it references `{{ outputs }}`, that folder starts empty, and once the command succeeds every file it wrote there
(matching `outputs`) becomes a file of the site at the same path, `out/blog/a.png` being `/blog/a.png`. An optional
`douze-outputs.json`, with the same shape, gives files a content type and data. The consumed files are replaced by the
outputs: a step that should keep them uses `match` to consume only what it needs. A command that uses neither (the
Pagefind example) gets nothing on disk, and its step emits no files. stdout is shown with `--verbose`.

The folders are under `<source>/.douze/tmp/` (`--cache-dir`), and are deleted after the run unless `--verbose`.

## worker

A worker is a long-running process that reads requests on stdin and writes answers on stdout, one JSON object per
line (UTF-8, `\n` terminated, protocol 1). It starts with the step and stops when the step is done.

1. Handshake: Douze sends `hello`; the worker answers `ready` within 30 seconds. `options` is the step's `with`.
   `concurrency` is how many requests the worker takes at once (1 when omitted); Douze sends at most the smaller of
   both.

   ```json
   {"type":"hello","protocol":1,"step":"twoslash","concurrency":8,"options":{},"site":"/abs/_site","source":"/abs/src"}
   {"type":"ready","protocol":1,"concurrency":4,"name":"twoslash-worker"}
   ```

2. One request per file, answered in any order. `encoding` is `utf8` for text and `base64` for binary content. A
   `result` has zero files (the file is dropped), one or many. In a file, an omitted `path`, `content` or
   `contentType` keeps the request's (a new path without `contentType` gets the type of its extension), and
   `metadata` is merged over the file's data. An `error` fails that file only, and `line` and `column` are shown.

   ```json
   {"type":"file","id":"42","path":"/docs/a.md","contentType":"text/markdown","encoding":"utf8","content":"# Title\n...","metadata":{"title":"A"}}
   {"type":"result","id":"42","files":[{"path":"/docs/a.md","encoding":"utf8","content":"# Title\n...","metadata":{"twoslash":true}}]}
   {"type":"error","id":"42","message":"TS2304: Cannot find name 'foo'","file":"/docs/a.md","line":12,"column":3}
   ```

3. Logs, at any time, shown with the step name (`level`: `debug`, `info`, `warn` or `error`):

   ```json
   {"type":"log","level":"info","message":"loaded aspire.d.ts in 1.2 s"}
   ```

4. Shutdown: after the last answer Douze sends `shutdown` and closes stdin; the worker exits with code 0 within 10
   seconds, or it is killed.

   ```json
   {"type":"shutdown"}
   ```

Unknown message types are ignored. A line that is not JSON, output on stderr (with `stderr: fail`), or a worker that
exits before `shutdown` fails every file in flight and the step: `worker exited with code 1: <end of stderr>`.

`douze worker-helper > scripts/douze-worker.mjs` writes a small Node helper that speaks the protocol:

```js
// scripts/twoslash-worker.mjs
import { serve } from './douze-worker.mjs';

serve(async (file, ctx) => {
  // file: { path, contentType, metadata, content } (a string, or a Buffer for binary content)
  // ctx: { step, options, site, source, log(message, level) }
  return { content: await transform(file.content), metadata: { twoslash: true } };
}, { concurrency: 4, name: 'twoslash-worker' });
```

The handler returns the new content (a string or a Buffer), a file (`{ content, path, contentType, metadata }`, every
member optional), an array of files, or `null` to drop the file. A thrown error fails that file; give it `line` and
`column` properties to point at a position.

# progress and tracing

Not available yet: a live console view, `--trace <file.json>` (Chrome trace events) and `--timeline <file.html>`.
