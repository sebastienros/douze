---
title: Migrate to Douze
description: How a site built with Eleventy, Jekyll or Hugo moves to Douze, and what every port has in common.
liquid: false
---
Douze reads the same kind of site as Eleventy and Jekyll: Markdown and HTML pages with YAML front matter, Liquid
templates, layouts and includes in `_includes`, data files in `_data`, collections built from `tags`. A site written for
one of them moves to Douze mostly by moving folders and renaming variables. A Hugo site keeps its content and data, but
its Go templates are rewritten in Liquid.

The guides below map the concepts of each generator to Douze, list what Douze does not have, and port a small site
step by step:

- [From Eleventy](from-eleventy.md)
- [From Jekyll](from-jekyll.md)
- [From Hugo](from-hugo.md)

## What every port has in common

These differences apply whichever generator you come from.

### Put the pages in a content folder

Without a `_content` folder (or `site.content` in `douze.yml`), the content folder is the source folder itself, and
Douze publishes every file of it that no step claims: `README.md`, `.git/`, `node_modules/`, `.env`, `package.json`
included. Douze does not skip files that start with `_` or `.`, and does not read `.gitignore`. Move the pages and
assets into `_content/`, or point `site.content` at the folder that has them:

```yaml
# douze.yml
site:
  content: src
```

### Data files in the content folder are data

Every `.yml`, `.yaml`, `.json` and `.xml` file of the content folder is folder data: its top-level keys apply to the
pages of its folder and subfolders, and the file is not published. A `manifest.json` or a `browserconfig.xml` that the
site should serve as a file needs another name: `manifest.json.liquid` is rendered as a Liquid template and written as
`manifest.json`.

### Front matter is YAML

Front matter is a YAML block between two `---` lines. TOML (`+++`) and JSON front matter are not recognized: the block
stays in the page as text.

### Liquid output is HTML-encoded

The `liquid` step encodes what `{{ }}` writes as HTML (`encode: html`, its default), as Hugo's `html/template` does
and unlike Jekyll and Eleventy. A value that holds HTML is written with the `raw` filter (`{{ item.html | raw }}`), or
the whole step can be set to `encode: none`. The `content` of a layout and the output of includes and shortcodes are
not encoded again.

### Unknown filters do nothing

A filter that Douze does not know returns its input unchanged, without an error or a warning: `{{ title | slugify }}`
writes the title as it is. Check each filter you use against the [Liquid reference](../reference/liquid.md). The `liquid`
step's `strict: true` reports undefined variables (top-level names such as `post`), which helps to find what a port
missed, but not missing members (`page.title`) or unknown filters.

### Markdown is CommonMark until you configure it

Without a `markdown` section in `douze.yml`, Markdown is CommonMark with no extension: no tables, no footnotes, no
heading ids. Eleventy, Jekyll and Hugo all enable some extensions by default. Turn them on:

```yaml
markdown:
  extensions: [pipetables, strikethrough, footnotes, tasklists, autolinks, attributes]
```

Heading ids come from the [`headings` step](../reference/steps/headings.md), which needs a workflow with `steps`. See
[Markdown](../guide/markdown.md).

### There is no plugin API

Douze has no JavaScript, Ruby or Go API: no custom filters written in code, no plugins. What plugins do is covered by
built-in steps (highlighting, images, social images, headings, localization), by Liquid files (shortcodes, feeds,
sitemaps) and by [command steps](../guide/command-steps.md), which run any tool on the files of a step.

### URLs keep `index.html`

`url` is the output path of a page, `/blog/post/index.html`, not `/blog/post/`. Templates that link to pages write
`{{ post.data.url | replace: 'index.html', '' }}`, and the items of `collections` are `{ data }` objects: a page's
title is `post.data.title`.

## A checklist

1. Install Douze ([Installation](../guide/install.md)) and build the site as it is, with `douze --plain`, to see the
   first errors.
2. Move pages and assets into a content folder, layouts and includes into `_includes`, data files into `_data`.
3. Convert front matter to YAML and rename variables (`page.title` to `title`, `site.data.x` to `x`).
4. Replace unsupported tags and filters, and plugins with steps or templates.
5. Configure `markdown`, then add the steps the site needs (`headings`, `highlight`, images) with a `steps` section,
   starting from what `douze --print-workflow` prints.
6. Compare the output folder with the old one (`diff -r`) and fix the URLs that changed.

## See also

- [Project structure](../guide/project-structure.md)
- [Configuration overview](../guide/configuration.md)
- [Template data](../reference/template-data.md)
- [Liquid filters and tags](../reference/liquid.md)
- [douze.yml reference](../reference/configuration.md)
