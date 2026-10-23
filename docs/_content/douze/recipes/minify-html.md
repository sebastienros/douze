---
title: Minify HTML with a command step
description: Run html-minifier-terser on every page with a filter command step, and keep the result cached until the tool changes.
---

A `filter` command step runs a program once per file: the file's content goes to its stdin, and its stdout becomes the
new content. Any minifier that reads stdin and writes stdout fits. This recipe uses
[html-minifier-terser](https://github.com/terser/html-minifier-terser), a Node package.

## Install the minifier

Install it in the site's folder, so that the version is locked in `package-lock.json`:

```sh
npm install --save-dev html-minifier-terser
```

## Add the step

The step runs after `layout`, on the final pages, and `write` takes its output. Declaring `steps` replaces the
default workflow, so `douze.yml` lists every step (`douze --print-workflow` prints the default ones):

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
    mode: filter
    match: "**/*.html"
    run: "node_modules/.bin/html-minifier-terser --collapse-whitespace --remove-comments --minify-css true --minify-js true"
    sources: package-lock.json
  write:
    needs: minify
    uses: write
```

- `match: "**/*.html"` sends only the pages to the minifier; the other files pass through the step unchanged.
- The program of `run` is a path, looked up relative to the step's `cwd` (the source folder). Before the build, Douze
  checks that it exists, and `douze doctor` reports it.
- Each page's result is cached. `sources: package-lock.json` adds the lock file to the step's cache key, so that
  updating the minifier runs the step again on every page.

A page such as:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Home</title>
</head>
<body>
  <main>
    <h1>Home</h1>
    <p>Welcome to the site.</p>
  </main>
</body>
</html>
```

is written as:

```html
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Home</title></head><body><main><h1>Home</h1><p>Welcome to the site.</p></main></body></html>
```

## Errors

A non-zero exit code fails the page with the end of the program's stderr:

```
error: minify: /blog/a.html: command exited with code 2 for '/blog/a.html': <last 20 lines of stderr>
```

Output on stderr fails the page too, even with exit code 0. `stderr: warn` shows it as a warning instead, and
`stderr: ignore` drops it. Node's own warnings (`ExperimentalWarning`) go away with `env: { NODE_NO_WARNINGS: "1" }`.

## Many pages

A `filter` step starts one process per page, up to `-p` at once. Starting Node takes about 50 ms, which adds up over
thousands of pages. A `worker` step starts Node once and sends it every page: [Write a Node worker
step](node-worker.md) does the same minification in a worker.

## See also

- [Command steps](../guide/command-steps.md)
- [Command steps and workers](../reference/commands.md)
- [Write a Node worker step](node-worker.md)
- [Cache and incremental builds](../guide/cache.md)
