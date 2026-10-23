---
title: Built-in steps
description: Every built-in step of Douze, with its shape, default match and place in the default workflow.
---

A step of `douze.yml` with `uses: <name>` runs a built-in step, configured by its `with` options. This page lists
them all; each has its own page with every option.

:::tip[Run your own executable or script]
A step with `run:` instead of `uses:` runs any command: a script, a linter, a Node worker, a downloaded tool such as
Pagefind. It gets files one at a time (`mode: filter`), all at once in a folder (`mode: batch`), or over JSON lines
(`mode: worker`). See [Command steps and workers](../commands.md) and the [command steps guide](../../guide/command-steps.md).

```yaml title="douze.yml"
steps:
  minify:
    needs: layout
    run: "npx html-minifier-terser --collapse-whitespace"
    mode: filter
    match: "**/*.html"
```

:::

## All steps

| Step | Shape | Default `match` | Default workflow | What it does |
|---|---|---|---|---|
| [data](data.md) | reduce | `**/*.{yml,yaml,json,xml}` | `site-data`, `folder-data` | Reads data files as site data or folder data. |
| [frontmatter](frontmatter.md) | map | `**/*.{md,html,liquid}` | `pages` | Moves the front matter into the file's data and sets `page.date`. |
| [parse](parse.md) | map | `**/*.{json,yml,yaml,xml}` | | Parses a data file into the file's data, under a name. |
| [replace](replace.md) | map | `**/*.md`, `**/*.html` | | Replaces literal tokens with rendered values, code blocks included. |
| [permalink](permalink.md) | map | every file | `permalink` | Computes output paths and sets `url`. |
| [collect](collect.md) | reduce | `**/*.html`, `**/*.md` | `collections` | Builds `collections.all`, `collections.<tag>` and `collections.urls`. |
| [localize](localize.md) | map | `**/*.html`, `**/*.md` | | Sets `locale`, `lang`, `dir`, `alternates`, and makes fallback copies. |
| [paginate](paginate.md) | map | `**/*.{html,md,json,liquid,xml,txt}` | `paginate` | Expands a paginated page into one page per chunk. |
| [liquid](liquid.md) | map, or group with `template` | `**/*.{html,md,json,liquid,xml,txt}` | `render` | Renders files as Liquid templates, or renders a template per file or group. |
| [fork](fork.md) | map | every file | | Passes each file through and adds a copy at another path. |
| [markdown](markdown.md) | map | `**/*.md` | `markdown` | Renders Markdown to HTML. |
| [headings](headings.md) | map | `**/*.html` | | Gives headings ids, adds anchors, sets `headings`, `toc` and `page.heading`. |
| [highlight](highlight.md) | map | `**/*.html` | | Highlights code fences with TextMate grammars and themes. |
| [hover-typescript](hover-typescript.md) | reduce | `**/*.html` | | Adds TypeScript type information (twoslash) to fences. |
| [hover-csharp](hover-csharp.md) | reduce | `**/*.html` | | Adds C# hovers and errors to fences, with Roslyn. |
| [hover-go](hover-go.md) | reduce | `**/*.html` | | Adds Go hovers and errors to fences, with gopls. |
| [check-code](check-code.md) | map | `**/*.html` | | Checks that code samples and generated files are well formed (YAML, JSON, XML, Liquid, `douze.yml`). |
| [images](images.md) | map | `**/*.{png,jpg,jpeg,webp,avif,gif,svg}` | | Makes the variants of images and sets `image`. |
| [image-map](image-map.md) | reduce | every file | | Publishes `images`, the images by source url. |
| [pictures](pictures.md) | map | `**/*.html` | | Rewrites the `<img>` of known images into responsive markup. |
| [social-image](social-image.md) | map | `**/*.html`, `**/*.md` | | Renders an Open Graph image per page. |
| [layout](layout.md) | map | `**/*.html` | `layout` | Applies layouts. |
| [write](write.md) | map | every file | `write` | Writes files to the output folder. |
| [`run:` command steps](../commands.md) | map (`filter`, `worker`) or reduce (`batch`) | every file | | Runs an executable or script of your own on the files. |

Only `liquid` with a `template` supports `group-by`; on other built-in steps it is an error
(`step '<name>' does not support 'group-by'`). Steps that run external commands are on
[Command steps and workers](../commands.md).

## Shapes

| Shape | Runs | Example |
|---|---|---|
| map | One unit per file, as soon as the file arrives. | `markdown`, `layout`, `write` |
| group | One unit per group of files, with `group-by`. | `liquid` with `template` |
| reduce | One unit for every file the step consumes, once all of them arrived. | `collect`, `data`, the hover steps |

A reduce step is a barrier: the steps after it wait for it. Some reduce steps (`data`, `collect`, `image-map`)
publish site data instead of passing files on, so a step that needs both their data and the pages needs both
steps: `needs: [permalink, collections]`. See [Workflow architecture](../../guide/workflow-architecture.md).

## How steps are wired

```yaml title="douze.yml"
steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
  layout:
    needs: headings
    uses: layout
  write:
    needs: layout
    uses: write
```

- `inputs` selects the source files that enter a step; `needs` sends it the files that come out of other steps, and
  the data they publish.
- A step consumes the entering files its `match` selects, its default `match` when it has none. The other files pass
  through it unchanged to the steps that need it.
- `match` is matched against the current path of a file. After `permalink`, `docs/guide.md` is
  `/guide/index.md`, so a later step matches `**/*.md`, not `docs/**/*.md`.
- `with` takes the options of the step; an unknown option is an error with a suggestion
  (`unknown option 'anchor' for step 'headings'; did you mean 'anchors'?`).
- Files that no step consumes reach `write` as they are: images, stylesheets and scripts are copied.

`douze --print-workflow` prints the steps a build runs. The full schema of a step is in
[douze.yml](../configuration.md#steps).
