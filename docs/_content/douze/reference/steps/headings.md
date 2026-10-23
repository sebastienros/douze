---
title: headings
description: Gives every heading of an HTML page an id and sets headings, toc and page.heading.
---

`uses: headings` works on the HTML of a page, whatever produced its headings (Markdown, a template, a shortcode, an
include). It gives every heading an id, optionally adds anchor links, and sets `headings`, `toc` and `page.heading` for
the layout. It runs after `markdown` and before `layout`, and is not in the default workflow.

| | |
|---|---|
| Shape | map |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

- Scans the `h1` to `h6` elements of the page in one pass, so the headings of the page, its shortcodes and includes
  share one set of ids. Headings inside `pre`, `code`, `script`, `style`, `template` and comments are ignored, and so
  are those inside an element with one of the `skip-classes`.
- Keeps the ids headings already have (`## Title {#custom}` with the `attributes` extension), and reserves them first.
  Other headings get a GitHub style id: lowercase, characters other than letters, marks, digits, spaces, hyphens and
  underscores removed, spaces replaced by hyphens (`What's new?` is `whats-new`). A duplicate gets `-1`, `-2`...
  (a second `Setup` is `setup-1`).
- With `anchors: true`, appends an anchor to each heading:

  ```html
  <h2 id="setup">Setup<a class="heading-anchor" href="#setup"><span aria-hidden="true">#</span><span class="sr-only">Link to this heading</span></a></h2>
  ```

- With `wrapper-class`, wraps each heading in `<div class="<wrapper-class> level-hN">`, and puts the anchor (when
  `anchors` is true) after the heading inside that element instead of inside the heading.
- Sets on the file:

| Key | Value |
|---|---|
| `headings` | Every heading, in order: `depth` (1 to 6), `text` (its text without tags) and `id` |
| `toc` | The headings of `levels` as a tree: each entry has `depth`, `text`, `id` and `children`, a heading being a child of the closest previous heading of a lower depth |
| `page.heading` | The text of the first `h1`, when the page has one |

A page without headings passes through unchanged. A layout renders `toc` with the [`toc` filter](../liquid.md).

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `anchors` | boolean | `false` | Appends an anchor link to each heading |
| `anchor-class` | string | `heading-anchor` | The class of the anchor |
| `anchor-label` | string | `Link to this heading` | The screen reader text of the anchor, in a `sr-only` span; `{text}` is replaced by the heading's text |
| `anchor-html` | string | `#` | The visible content of the anchor, in an `aria-hidden` span (raw HTML, such as an SVG) |
| `wrapper-class` | string | none | Wraps each heading and its anchor in `<div class="<wrapper-class> level-hN">` |
| `levels` | integer or list of integers | `[2, 3]` | The heading levels of `toc`, from 1 to 6 |
| `skip-classes` | string or list of strings | none | Headings inside an element with one of these classes (a component) are left alone |

## Errors

```
option 'levels' of step 'headings' must be a list of heading levels from 1 to 6
```

## Example

```yaml title="douze.yml"
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
      levels: [2, 3]
      anchor-label: "Link to {text}"
  layout:
    needs: headings
    uses: layout
  write:
    needs: layout
    uses: write
```

```liquid
<h1>{{ page.heading | default: title }}</h1>
{{ toc | toc: class: "toc", max-depth: 3 }}
{{ content }}
```

## Related

- [Template data: `headings`, `toc`, `page.heading`](../template-data.md)
- [Liquid filters: `toc`](../liquid.md)
- [markdown](markdown.md), [layout](layout.md)
- [Navigation and sidebars](../../guide/navigation.md)
