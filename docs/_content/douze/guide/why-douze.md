---
title: Why Douze?
description: What Douze is, the principles it is built on, and the sites it is a good fit for.
---

Douze is a static site generator. It reads a folder of Markdown, HTML and Liquid files, data files and assets, and
writes a folder of static files that any web server can host. It is a single native executable, with no runtime to
install: no .NET, no Node.

```sh
douze serve      # build, watch, and serve on http://localhost:8080/ with live reload
douze            # build the site to _site
```

## Design principles

### No configuration to start

A folder with an `index.md` is a site. Douze detects the conventional folders (`_content`, `_data`, `_includes`) and
runs a default workflow that covers front matter, permalinks, collections, pagination, Liquid, Markdown and layouts.
`douze.yml` is optional, and every section of it is optional too: a site adds it when it needs a setting, such as
`site.url`, or its own steps.

### Fast

- Douze is compiled ahead of time to native code: it starts in milliseconds and needs no warmup.
- Files flow through the steps of the workflow as soon as they are ready, and the steps run in parallel, up to one
  unit of work per processor by default (`-p`).
- Every build stores the result of each unit of work in a disk cache (`.douze/`). The next build, and every rebuild of
  `douze serve`, runs only what a change affects: a second build without changes runs nothing.

See [Benchmarks](benchmarks.md) for measurements on large sites.

### Eleventy-like conventions

Douze follows [Eleventy](https://www.11ty.dev) where it can, so its model is familiar:

- Liquid runs before Markdown, so a Markdown page can use tags and variables.
- `layout`, `permalink`, `tags`, `collections.all`, `collections.<tag>` and `pagination` work as in Eleventy.
- Data cascades from the page's front matter to the data files of its folders, then to the site data of `_data`.
- Collections are sorted by date, then by source path.

See [Migrate from Eleventy](../migrate/from-eleventy.md) for the differences.

### Deterministic builds

Documents are processed in parallel, but the output does not depend on the order in which they finish: whatever is
collected across documents (collections, sidebars, image maps) is sorted before templates see it. The same sources
build the same site, byte for byte.

### A workflow you can see and change

The build is a graph of steps that files flow through. `douze --print-workflow` prints it as YAML, and `douze.yml`
can replace it: add a step, remove one, run an external tool on some files, or generate pages from data. See
[Workflow architecture](workflow-architecture.md).

This documentation site is built with Douze and a workflow of its own: its sources and its `douze.yml` are in the
[`docs` folder](https://github.com/sebastienros/douze/tree/main/docs) of the repository.

### One binary

The native executable contains everything the built-in steps need, including libvips for images and the TextMate
grammars for syntax highlighting. Tools that a step downloads on first use, such as Pagefind or the TypeScript
language server, are pinned and verified, and `douze tools install` downloads them ahead of time. A site needs Node
only when its own [command steps](command-steps.md) run Node scripts.

## When Douze is a good fit

- Documentation sites, blogs and marketing sites written in Markdown, with Liquid layouts.
- Large sites, with thousands or tens of thousands of pages, where build time and memory matter.
- Sites generated from data: API references, catalogs, pages built from JSON or YAML files.
- Teams that want builds without a `node_modules` folder, or a CI image that only needs one executable.

## When it is not

- Single page applications, or sites whose pages are mostly client-side components. Douze renders HTML at build time
  and has no component framework or JavaScript bundler. A [command step](command-steps.md) can call one, but a
  framework such as Astro, Next.js or SvelteKit is a better fit.
- Sites that render on the server for each request. Douze only writes static files.
- Sites that depend on a large ecosystem of plugins of another generator. Douze's extension points are its built-in
  steps, Liquid shortcodes and command steps.
- Projects that need a stable release today. Douze has no stable release yet, and breaking changes happen.

## See also

- [Installation](install.md)
- [Project structure](project-structure.md)
- [Workflow architecture](workflow-architecture.md)
- [Build a blog](../tutorial/index.md), the tutorial
