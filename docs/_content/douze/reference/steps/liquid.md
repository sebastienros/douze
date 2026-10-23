---
title: liquid
description: Renders pages as Liquid templates, or a template of the includes folder once per file or group.
liquid: false
---

`uses: liquid` renders Liquid templates. Without `template`, it renders the content of each file as a template whose
model is the file's data, so that pages, feeds and other text files can use variables, tags, filters and shortcodes.
With `template`, it renders a template of the includes folder once per file, or once per group with `group-by`, and
emits the result as a new file at the path rendered from `permalink`: a page per data file, or an index of a folder.

| | |
|---|---|
| Shape | map; group with `group-by` (only with `template`) |
| Default `match` | `**/*.html`, `**/*.md`, `**/*.json`, `**/*.liquid`, `**/*.xml`, `**/*.txt` |
| Default workflow | yes, as step `render` (after `paginate`, before `markdown`) |

## What it does

### Rendering pages (no `template`)

- The file's content is the template. Names resolve through the file's data, then its folder data, the site data and
  `site` (see [name resolution](../template-data.md)).
- A file whose `liquid` resolves to `false` (in its front matter, or in folder or site data) passes through unchanged.
- In a Markdown file (content type `text/markdown`), fenced code blocks are not Liquid by default
  (`protect: fences`): they are set aside before parsing and restored in the output, so `{{ }}` in a code sample is
  kept as written. Inline code is not protected; use `{% raw %}` or `liquid: false` for it.
- Variables captured with `{% capture name %}` are added to the file's data, so its layout can read them. In a
  Markdown page they hold Markdown source, which no later step renders.
- The values of output tags (`{{ }}`) are encoded for the file's content type, read from its path (after
  `permalink`): see [output encoding](#output-encoding).
- The path and the other data of the file do not change.

### Rendering a template (`template`)

- Per file (no `group-by`), the template and the `permalink` see `file`: `path`, `name`, `dir`, `ext`, `content` (null
  for a binary file) and `data` (the file's data).
- Per group (`group-by: file`, `dir`, or a Liquid template), they see `group`: `key` (the group key) and `files`, a
  list of the same objects as `file`.
- Other names (site data, `collections`, folder data of the first file's folder) resolve as in a page.
- The new file's path is the rendered `permalink`; a result without an extension is a folder, and `index.html` is
  added. Its data is `url` (the path) and the keys of `data`.
- The consumed files are replaced by the rendered file.

With `group-by`, the template typically loops over `{% for f in group.files %}`, and the permalink can use
`{{ group.key }}`.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `template` | string | none | A template path in the includes folder. Switches the step to template rendering; needs `permalink` |
| `permalink` | string (Liquid) | none | With `template`: the output path, rendered with `file` or `group` |
| `data` | mapping | none | With `template`: merged into the new file's data |
| `encode` | `auto`, `html`, `xml`, `json` or `none` | `auto` | How the values of output tags are encoded. `auto` picks the encoder of each output's content type; the others force one for every file of the step (see [output encoding](#output-encoding)) |
| `protect` | `fences` or `none` | `fences` | `none` renders the fenced code blocks of Markdown files as Liquid too |
| `strict` | boolean | `false` | An undefined variable is an error instead of nothing. Shortcode templates are never strict |

## Output encoding

With `encode: auto`, the encoder depends on the extension of the output path (the rendered `permalink` with
`template`):

| Output | Encoder | `{{ 'Tom & "Jerry" + 1' }}` |
|---|---|---|
| `.html`, `.md`, `.liquid` | HTML: `&`, `<`, `>`, `"`, `'` and `+` as entities | `Tom &amp; &quot;Jerry&quot; &#x2B; 1` |
| `.xml`, `.svg` and other XML types | XML: `&`, `<`, `>`, `"` and `'` as entities | `Tom &amp; &quot;Jerry&quot; + 1` |
| `.json` | the content of a JSON string: `"`, `\` and control characters escaped | `Tom & \"Jerry\" + 1` |
| anything else (`.txt`, `.css`, `.js`) | none | `Tom & "Jerry" + 1` |

Non-ASCII characters are written as they are in every case. Filters whose result is already encoded are not encoded
again: `xml_escape`, `json`, `escape`, and the filters that return markup (`markdownify`, `excerpt`, `toc`). So a feed
can use `{{ title }}` or `{{ title | xml_escape }}` alike, a date keeps its `+`, and a JSON file writes
`"title": "{{ title }}"` or `"title": {{ title | json }}`.

## Caching

A page depends on the names it resolved (its own keys, folder and site data, collections), on the includes, layouts
and shortcodes it used, and on the images its `image` tags read. The `markdown` section of `douze.yml` is part of the
step's version (tags read its image defaults), so changing it renders every page again.

## Errors

```
step 'render' uses 'liquid' with 'template' but 'permalink' is missing
step 'render' uses 'liquid' with 'permalink' but 'template' is missing
option 'encode' of step 'render' must be 'auto', 'html', 'xml', 'json' or 'none', not 'url'
option 'protect' of step 'render' must be 'fences' or 'none', not 'all'
option 'data' of step 'render' must be a mapping
step 'render' does not support 'group-by'
```

The last one is reported for `group-by` without `template`. At build time, a missing template fails the unit with
`template 'package.liquid' not found in '/_includes/'`, and with `strict: true` an unknown name fails with
`undefined variable 'name'`. An unknown tag close to a shortcode name suggests it.

## Example

Render pages strictly, and make one page per JSON file plus an index of them:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  render:
    needs: pages
    uses: liquid
    with:
      strict: true
  markdown:
    needs: render
    uses: markdown
  permalink:
    needs: markdown
    uses: permalink
    with:
      root: docs
  layout:
    needs: permalink
    uses: layout

  packages:
    inputs: "data/packages/*.json"
    uses: parse
    with:
      as: package
  package-pages:
    needs: packages
    uses: liquid
    with:
      template: package.liquid
      permalink: "packages/{{ file.data.package.name }}/"
  package-index:
    needs: packages
    uses: liquid
    group-by: dir
    with:
      template: packages.liquid
      permalink: "packages/index.html"
      data:
        kind: index

  write:
    needs: [layout, package-pages, package-index]
    uses: write
```

```liquid
{%- comment -%} _includes/packages.liquid {%- endcomment -%}
<ul>{% for f in group.files %}<li>{{ f.data.package.name }}</li>{% endfor %}</ul>
```

## Related

- [Template data](../template-data.md) and [Liquid filters and tags](../liquid.md)
- [Step fields: `group-by`](../configuration.md)
- [markdown](markdown.md), [layout](layout.md), [paginate](paginate.md),
  [parse](parse.md)
- Guides: [Pages](../../guide/pages.md), [Includes and shortcodes](../../guide/shortcodes.md), [Data files](../../guide/data.md)
