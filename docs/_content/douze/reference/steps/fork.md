---
title: fork
description: Passes each file through and adds a copy at another path, with extra data or other content.
liquid: false
---

`uses: fork` passes each file through unchanged and adds a copy of it at another path, with extra data and,
optionally, other content. Use it to publish a second form of a page, such as the Markdown source of every page next
to its HTML, taken after Liquid ran and before Markdown renders it.

| | |
|---|---|
| Shape | map |
| Default `match` | every file (`**`) |
| Default workflow | no |

## What it does

For each consumed file, the step emits two files: the file itself, unchanged, and the copy.

- The copy's path is `permalink`, a Liquid template rendered with the file's data (`{{ url }}`, `{{ title }}`...). A
  result without an extension is a folder, and `index.html` is added. An empty result fails the unit, and so does a
  result with an empty segment (`/a//b.md`) or an empty file name (`/.txt`): they come from a value of the template
  that rendered nothing. So a fork cannot write a file whose name is a dot and an extension, such as `/.nojekyll`
  or `/.htaccess`.
- The copy's data is the file's, with `data` merged over it, and `url` set to the copy's path.
- The copy's content is the file's, or `content` rendered with the file's data and the file's text as `content`.
- With `strip-regions` (the default), the lines that are a single U+001E character, the markers of raw HTML regions
  that block shortcodes leave in a Markdown page, are removed from the copy's text.
- A file for which `skip`, a Liquid condition, is true gets no copy (it still passes through).

Templates render without HTML encoding, so paths and Markdown keep their characters.

The copy goes on to the next steps like any other file: give those steps a `match` that leaves it out when they
should not process it (a `markdown` step that must not render the Markdown twin, for instance).

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `permalink` | string (Liquid) | required | The copy's path, rendered with the file's data |
| `data` | mapping | none | Merged over the copy's data |
| `content` | string (Liquid) | the file's content | The copy's content, rendered with the file's data and `content`, the file's text |
| `strip-regions` | boolean | `true` | Removes the lines that are a single U+001E character from the copy |
| `skip` | string (Liquid condition) | none | A condition such as `draft` or `twin == false`; when it is true the file gets no copy |
| `strict` | boolean | `false` | An undefined variable of `permalink` or `content` is an error instead of nothing |

## Errors

```
step 'twins' needs the option 'permalink'
option 'data' of step 'twins' must be a mapping
the permalink of step 'twins' is empty for '/docs/guide/a.md'
the permalink of step 'twins' is '/docs/guide/.md' for '/docs/guide/a.md': it has an empty file name, a value of the template is probably undefined
undefined variable 'slug'
```

The last one is with `strict: true`, for a template that reads `slug` when the file does not define it. Errors
about a file fail its unit, and are reported as `error: twins: /docs/guide/a.md: ...`.

## Example

The Markdown twin of every page, written at `page.md` next to its `index.html`. In this workflow `permalink` runs
first, so `url` is known and the paths no longer start with the content folder:

```yaml title="douze.yml"
site:
  content: docs
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
  twins:
    needs: render
    uses: fork
    match: "**/*.md"
    with:
      permalink: "{{ url | replace: 'index.html', 'page.md' }}"
      data:
        twin: true
      skip: "draft"
  markdown:
    needs: twins
    uses: markdown
    match: ["**/*.md", "!**/page.md"]
  write:
    needs: markdown
    uses: write
```

`docs/guide/b.md` is written to `guide/b/index.html` and `guide/b/page.md`; a page with `draft: true` gets no twin.

## Related

- [liquid](liquid.md), [markdown](markdown.md), [permalink](permalink.md)
- [Step fields: `match`](../configuration.md)
- [Feeds, sitemaps and other outputs](../../guide/outputs.md)
