---
title: markdown
description: Renders Markdown files to HTML with the site's Markdown pipeline.
---

`uses: markdown` renders Markdown files to HTML with the site's Markdown pipeline, configured by the `markdown`
section of `douze.yml`, and changes their extension to `.html`, so that the steps after it (`headings`, `highlight`,
`layout`) match them on `**/*.html`.

| | |
|---|---|
| Shape | map |
| Default `match` | `**/*.md` |
| Default workflow | yes, as step `markdown` (after `render`, before `layout`) |

## What it does

- Renders the file's content with Markdig and the pipeline of the `markdown` section: its extensions, asides, code
  fence markup, `.md` link rewriting and raw regions. Without a `markdown` section, Markdown is CommonMark with no
  extension (Douze's own fence markup, raw regions and link rewriting still apply).
- Changes the path's extension to `.html` (`/blog/post.md` becomes `/blog/post.html`, or `/blog/post/index.md`
  becomes `/blog/post/index.html` when `permalink` ran first). The data does not change.
- A file whose `markdown` resolves to `false` (front matter, folder data or site data) passes through unchanged.
- A Markdown page whose `permalink` is not HTML (`feed.xml`) is not rendered: after the `permalink` step its path no
  longer ends with `.md`, so the default `match` leaves it out.
- Markdown warnings, such as an unknown aside kind, are logged with the source file and position.

Heading ids are not set here (Markdig's `autoidentifiers` is not available): the [headings](headings.md)
step gives every heading an id, except those set with `{#id}` and the `attributes` extension.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `extensions` | string or list of strings | the `markdown` section's | Replaces the extensions for this step and keeps the section's other settings. A string can join names with `+` (`pipetables+footnotes`). `none` is no extension, `advanced` is Markdig's set without `autoidentifiers` |

The names are those of the `markdown.extensions` key: see [`markdown` in douze.yml](../configuration.md).

## Caching

The step's version includes the hash of the Markdown options (the section, or the step's `extensions`), so changing
them renders every page again. A page also depends on the `.md` files its relative links point to: a link to a file
that does not exist is left as written, and the page renders again when the file is created.

## Errors

```
option 'extensions' of step 'markdown': unknown Markdown extension 'tabels'; did you mean 'pipetables'?
option 'extensions' of step 'markdown': use the headings step for heading ids
option 'extensions' of step 'markdown' must be a string or a list of strings
```

The second one is for `autoidentifiers`.

## Example

```yaml title="douze.yml"
markdown:
  extensions: [pipetables, footnotes, alerts, customcontainers, attributes]
steps:
  pages:
    inputs: ["**/*.md", "!notes/**"]
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  notes:
    inputs: "notes/**/*.md"
    uses: markdown
    with:
      extensions: none
  permalink:
    needs: [markdown, notes]
    uses: permalink
  write:
    needs: permalink
    uses: write
```

## Related

- [`markdown` section of douze.yml](../configuration.md)
- [liquid](liquid.md), [headings](headings.md), [highlight](highlight.md),
  [layout](layout.md)
- Guides: [Markdown](../../guide/markdown.md), [Syntax highlighting](../../guide/syntax-highlighting.md)
