---
title: layout
description: Wraps HTML pages in their layouts, following the layout chain.
liquid: false
---

`uses: layout` wraps HTML pages in their layouts: it renders the layout a page names in `layout` with the page's HTML
as `content`, then the layout named in that layout's front matter, and so on.

| | |
|---|---|
| Shape | map |
| Default `match` | `**/*.html` |
| Default workflow | yes, as step `layout` (after `markdown`, before `write`) |

## What it does

- Resolves `layout` like any name of the page: its front matter, its folder data and the parent folders' data, then
  the site data. A folder data file (`blog/blog.yml` with `layout: post`) or `_data/layout.yml` can set it for many
  pages. A page without a `layout` (or with an empty one) passes through unchanged.
- Loads the layout from the includes folder (or `includes`). A name without an extension is tried as is, then with
  `.liquid`, then with `.html`.
- Renders it with the page's data and `content`, the page's HTML, which `{{ content }}` writes as it is. Values are
  HTML encoded otherwise.
- When the layout's front matter has a `layout`, renders that one with the result as `content`, up to 32 levels.
- Names resolve through the page's data and scopes first, then through the front matter of the layouts, the innermost
  layout first: a layout's front matter gives defaults that a page can override.
- Only the content changes; the path and data of the page do not.

Layouts apply to HTML files only: a feed or a sitemap (`feed.xml.liquid`) is not laid out unless the step's `match`
includes it.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `includes` | string | the site's includes folder | Another folder of the source folder to load layouts from |

## Caching

A page depends on the layouts it used (and the includes and shortcodes they used) and on the names it resolved.
Editing a layout lays out again the pages that use it. The `markdown` section of `douze.yml` is part of the step's
version, since tags such as `image` read its defaults.

## Errors

```
layout 'post' not found in '/_includes/' (used by '/_content/blog/a.md')
layout chain is longer than 32: base -> page -> base -> ...
```

These fail the page at build time.

## Example

```yaml title="douze.yml"
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  layout:
    needs: markdown
    uses: layout
    with:
      includes: theme/layouts
  write:
    needs: layout
    uses: write
```

```liquid
---
layout: base
section: docs
---
<article data-section="{{ section }}">{{ content }}</article>
```

Saved as `theme/layouts/page.liquid`, this layout is used by pages with `layout: page`, and is itself wrapped in
`theme/layouts/base.liquid`.

## Related

- [Template data](../template-data.md)
- [markdown](markdown.md), [headings](headings.md), [write](write.md)
- Guides: [Layouts](../../guide/layouts.md), [Includes and shortcodes](../../guide/shortcodes.md)
