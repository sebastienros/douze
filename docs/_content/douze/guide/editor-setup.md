---
title: Editor setup
description: Set up an editor for Liquid, YAML and Markdown files of a Douze site.
liquid: false
---

A Douze site is plain files: Markdown, HTML, Liquid templates and YAML. Any editor works. This page has tips to get
syntax highlighting, validation and previews that match how Douze reads those files, with VS Code as the example.

## Extensions for VS Code

| Extension | For |
|---|---|
| [Liquid](https://marketplace.visualstudio.com/items?itemName=sissel.shopify-liquid) (`sissel.shopify-liquid`) | Highlighting and formatting of Liquid in `.liquid`, `.html` and `.md` files |
| [YAML](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml) (`redhat.vscode-yaml`) | Syntax checks of `douze.yml`, front matter and data files |

Douze uses [Fluid](https://github.com/sebastienros/fluid), which implements Shopify's Liquid: extensions made for
Shopify themes highlight it correctly. Douze's own tags (shortcodes, `include_markdown`, `image`, `error`) are
highlighted like any other tag. A Shopify extension may flag them, or Douze's filters, as unknown: that is a warning
of the extension, not of Douze.

## File associations

Layouts and includes can be `.liquid` or `.html` files, and output templates are named after their output
(`feed.xml.liquid`, `llms.txt.liquid`). If your editor does not recognize Liquid in some of them, associate the
extensions in `.vscode/settings.json`:

```json title=".vscode/settings.json"
{
  "files.associations": {
    "*.liquid": "liquid",
    "_includes/**/*.html": "liquid"
  },
  "emmet.includeLanguages": {
    "liquid": "html"
  }
}
```

`emmet.includeLanguages` keeps Emmet's HTML abbreviations in Liquid files.

## `douze.yml`

Douze does not publish a JSON schema for `douze.yml`, so editors do not complete its keys. Douze validates the file
itself, before every build, and reports every error with its position:

```txt
douze.yml(6,43): error: unknown field 'if' on step 'render'
```

Two habits help:

- `douze --print-workflow` prints the workflow a build runs, the default steps included, in the format of the file. It
  is a starting point to copy from, and a quick way to check a change, since it fails on an invalid file.
- The [douze.yml reference](../reference/configuration.md) and the [built-in steps](../reference/steps/index.md) list
  every key and option.

With `douze serve` running, an invalid `douze.yml` prints its errors as soon as you save it, and the session waits for
the next change.

## Markdown previews

The Markdown preview of an editor renders CommonMark, not what Douze renders:

- Liquid tags and variables (`{% badge "New" %}`, `{{ site.url }}`) are shown as text. Douze runs Liquid before
  Markdown.
- Containers (`:::tip`), heading ids and the other extensions of the `markdown` section render differently, or not
  at all.
- Links between `.md` files work in the preview, and Douze rewrites them to the URLs of the pages (`links: true`), so
  writing them that way keeps both working.

`douze serve` is the preview that matches the site: it rebuilds the page on save and reloads it in the browser.

## Formatting

Formatters such as Prettier may reflow Liquid in `.html` and `.md` files, or change the spacing in tags. When a
formatter breaks a template, exclude the includes folder from it, or use a Liquid plugin for the formatter.

## See also

- [Configuration overview](configuration.md)
- [Develop and build](develop-and-build.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Markdown](markdown.md)
