---
title: Write a Node worker step
description: Transform pages in one long-running Node process with a worker step and the helper that douze worker-helper prints.
---

A `worker` command step starts one process for the step and sends it every file over JSON lines on stdin and stdout.
Node starts once instead of once per file, and the worker keeps what it loads (a library, a compiler, a cache) for the
whole build. `douze worker-helper` prints a small Node module that speaks the protocol, so a worker is a single
function.

This recipe minifies every page with html-minifier-terser, like [the filter recipe](minify-html.md), in a
worker.

## Get the helper

```sh
mkdir -p scripts
douze worker-helper > scripts/douze-worker.mjs
npm install --save-dev html-minifier-terser
```

## Write the worker

```js title="scripts/minify-worker.mjs"
import { minify } from 'html-minifier-terser';
import { serve } from './douze-worker.mjs';

serve(async (file, ctx) => {
  try {
    return await minify(file.content, ctx.options);
  } catch (e) {
    ctx.log(`${file.path}: ${e.message}`, 'warn');
    return file.content;
  }
}, { concurrency: 8, name: 'minify' });
```

The handler receives `file` (`path`, `contentType`, `metadata`, and `content`: a string for text, a Buffer otherwise)
and `ctx` (`step`, `options`, `site`, `source`, and `log(message, level)`). It returns:

- a string or a Buffer: the file's new content,
- an object `{ content, path, contentType, metadata }`, every member optional: what it leaves out keeps the input's
  value, and `metadata` is merged over the file's data,
- an array of those, for several files,
- `null`, to drop the file.

A thrown error fails that file only. Give the error `line` and `column` properties to point at a position:

```js
const e = new Error('no title');
e.line = 3;
e.column = 1;
throw e;
```

```
error: minify: /index.html: no title (line 3, column 1)
```

`concurrency` is how many files the worker takes at once; Douze sends at most the smaller of it and the build's parallelism (`-p`).

## Add the step

```yaml title="douze.yml"
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  layout:
    needs: markdown
    uses: layout
  minify:
    needs: layout
    mode: worker
    match: "**/*.html"
    run: "node scripts/minify-worker.mjs"
    sources: [scripts/*.mjs, package-lock.json]
    with:
      collapseWhitespace: true
      removeComments: true
      minifyCSS: true
      minifyJS: true
  write:
    needs: minify
    uses: write
```

- `with` is sent to the worker in its `hello` message: it is `ctx.options`, here the options of `minify()`.
- `sources` adds files to the step's cache key: editing the worker or updating the package runs the step again on
  every page. Without it, a page is processed again only when it changes or the step changes in `douze.yml`.
- `--verbose` shows the handshake and the logs of the worker:

  ```
  minify: worker 'minify' ready, 8 request(s) at once
  ```

## Rules of the protocol

The helper handles the protocol; a worker written in another language follows the same rules, which the
[reference](../reference/commands.md) describes in full:

- The worker answers `hello` with `ready` within 30 seconds.
- Each line is one JSON object, UTF-8, ending with `\n`. A line on stdout that is not JSON fails the step, so a
  worker writes its logs with `ctx.log` (a `log` message), never with `console.log`.
- Output on stderr fails the step, unless `stderr` is `warn` or `ignore`.
- After the last file, Douze sends `shutdown`; the worker exits with code 0 within 10 seconds, or it is killed.
- With `timeout`, each file has that long.

## See also

- [Command steps](../guide/command-steps.md)
- [Command steps and workers](../reference/commands.md)
- [Minify HTML with a command step](minify-html.md)
- [douze worker-helper](../reference/cli.md)
