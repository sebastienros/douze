---
title: Add search with Pagefind
description: Index the built pages with Pagefind in a command step that Douze downloads and runs, and add the search UI to a layout.
liquid: false
---

[Pagefind](https://pagefind.app) indexes the HTML of a built site and writes a static search index and UI next to it.
A command step with `tool: pagefind` runs it: Douze downloads Pagefind's binary for the platform on first use, so the
site needs neither Node nor npm.

## Add the step

A command step runs after `layout`, on the final HTML. Declaring `steps` replaces the default workflow, so `douze.yml`
lists every step, the default ones printed by `douze --print-workflow` plus `search`:

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
  search:
    needs: layout
    tool: pagefind
    match: "**/*.html"
    run: "{{ tool }} --site {{ inputs }} --output-path {{ outputs }}/pagefind"
  write:
    needs: [layout, search]
    uses: write
```

How it fits together:

- `search` is a `batch` step (the default `mode`): one process for all the pages. `{{ inputs }}` is a folder that
  holds the HTML pages the step consumes, at their paths, and `{{ outputs }}` an empty folder whose files become files
  of the site once the command succeeds: `outputs/pagefind/pagefind.js` is `/pagefind/pagefind.js`.
- `{{ tool }}` is the path of the Pagefind binary.
- A batch step replaces the files it consumes with its outputs, so `write` needs both `layout`, for the pages, and
  `search`, for the index. `match: "**/*.html"` keeps the other files (images, stylesheets) out of the index folder.

The first build downloads Pagefind:

```
✓ layout            2 files    0.00 s
search: downloading pagefind 1.5.2 (@pagefind/darwin-arm64) from https://registry.npmjs.org/@pagefind/darwin-arm64/-/darwin-arm64-1.5.2.tgz
search: installed pagefind 1.5.2 in /my-site/.douze/tools/pagefind/1.5.2/osx-arm64
✓ search            2 files    0.49 s
✓ write            18 files    2.34 s
```

`_site/pagefind/` then has the index, `pagefind-ui.js` and `pagefind-ui.css`.

## Add the search UI

Load the UI from the layout and mark the part of the page to index with `data-pagefind-body`, so that headers and
navigation are not indexed:

```liquid
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>{{ title }}</title>
  <link href="/pagefind/pagefind-ui.css" rel="stylesheet">
  <script src="/pagefind/pagefind-ui.js"></script>
</head>
<body>
  <div id="search"></div>
  <script>
    window.addEventListener('DOMContentLoaded', () => new PagefindUI({ element: '#search' }));
  </script>
  <main data-pagefind-body>
    {{ content }}
  </main>
</body>
</html>
```

Search works on the built site, with `douze serve` for instance. Pagefind's documentation describes the other
attributes (`data-pagefind-ignore`, `data-pagefind-meta`, filters) and the options of the UI.

## Pin or prepare the download

- `tool: pagefind` uses the version that Douze pins (1.5.2), verified against its integrity. `tool: pagefind@<version>`
  picks another release.
- The binary is installed under the cache folder, `<source>/.douze/tools/`. `douze doctor` reports it as `download`
  until then, and `douze tools install` downloads it ahead, for a CI cache or an offline machine:

  ```sh
  douze tools install
  ```

- The step runs again when one of its pages changes; a build without changes reuses its index from the cache.

## See also

- [Command steps](../guide/command-steps.md)
- [Command steps and workers](../reference/commands.md)
- [Cache and incremental builds](../guide/cache.md)
- [Troubleshooting](../guide/troubleshooting.md)
- [douze doctor and douze tools install](../reference/cli.md)
