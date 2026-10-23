---
title: Pages
description: The kinds of pages Douze builds, their front matter, how Liquid and Markdown render them, and how to turn either off.
liquid: false
---

A page is a file of the content folder that Douze renders: a Markdown file, an HTML file, or a Liquid template. Its
front matter sets its data, Liquid renders its variables and tags, Markdown turns it into HTML, and its layouts wrap
it. Pages can also come from data, one page per data file. This page describes each kind and the front matter keys
that change how a page renders.

## Kinds of pages

| File | Rendered as | Output |
|---|---|---|
| `about.md` | Liquid, then Markdown to HTML, then layouts | `about/index.html` |
| `about.html` | Liquid, then layouts | `about/index.html` |
| `feed.xml.liquid` | Liquid | `feed.xml` |
| `notes.liquid` | Liquid | `notes.liquid` |
| `robots.txt`, `api.json` | see below | |

- **Markdown pages** (`.md`) are the usual pages of a site.
- **HTML pages** (`.html`) are for pages that are mostly markup, such as a home page with a custom layout.
- **Liquid templates** (`name.ext.liquid`) produce a file of another type, written without `.liquid`: feeds,
  sitemaps, `llms.txt`, JSON. See [Feeds, sitemaps and other outputs](outputs.md). A `.liquid` file without a second
  extension keeps its name.

Other files of the content folder are handled by extension. `.txt` files are rendered as Liquid. `.json`, `.xml`,
`.yml` and `.yaml` files are folder data, and are not published: to publish one, write it as a template
(`api.json.liquid`). Everything else, such as images, stylesheets and fonts, is copied as it is.

## Front matter

A page starts with YAML front matter between `---` lines. Its keys are the page's data, which its template and its
layouts read:

```md title="_content/blog/first-post.md"
---
title: My first post
description: What this blog is about.
date: 2026-01-02
tags: [post, news]
layout: post.liquid
---
# {{ title }}

Published on {{ date | date: "%B %d, %Y" }}.
```

Some keys have a meaning for Douze:

| Key | Meaning |
|---|---|
| `layout` | The layout to apply, from the includes folder. See [Layouts](layouts.md). |
| `permalink` | The output path, a Liquid template, or `false`. See [Routing and permalinks](routing.md). |
| `tags` | The collections the page is in (`collections.post`). See [Collections](collections.md). |
| `date` | The date collections sort by; `page.date` is the file's modification time. |
| `pagination` | Makes the page one page per slice of a list. See [Pagination](pagination.md). |
| `liquid` | `false` leaves the page's Liquid as text. |
| `markdown` | `false` leaves a Markdown page as Markdown. |
| `locale` | Set by the `localize` step. See [Internationalization](internationalization.md). |

Front matter is not the only source: a key a page does not set comes from the data files of its folder and parent
folders, then from site data, so a folder data file can set `layout` or `tags` for every page of a folder. See
[Data files](data.md).

## Liquid runs before Markdown

As in Eleventy, a Markdown page is rendered as Liquid first, then as Markdown. A page can use variables, tags,
includes and shortcodes, and Markdown renders what they produce:

```md
{% for post in collections.post %}
- [{{ post.data.title }}]({{ post.data.url }})
{% endfor %}
```

The fenced code blocks of a Markdown page are not Liquid: a `{{ }}` in a code sample is kept as it is. Other `{{`
or `{%` that are not Liquid, such as inline code that shows Liquid syntax, need one of:

- `{% raw %}...{% endraw %}` around them;
- `liquid: false` in the front matter, which turns Liquid off for the whole page;
- `liquid: false` in a folder data file, which turns it off for every page of the folder.

A variable captured in a Markdown page (`{% capture %}`) holds Markdown source, which Markdown does not render
again: write it in HTML.

## Turn off Liquid or Markdown

```md title="_content/syntax.md"
---
title: Liquid syntax
liquid: false
---
Write `{{ title }}` to show the title.
```

```md title="_content/plain.md"
---
title: Plain
markdown: false
permalink: plain.md
---
# This file is published as Markdown
```

With `markdown: false`, the page is rendered as Liquid and written as a `.md` file: at its `permalink`, or at
`plain/index.md` without one. A Markdown page whose `permalink` is not HTML, such as `feed.xml`, is not rendered to
HTML either.

## Drafts

`permalink: false` leaves a page out of the output and out of the collections:

```md title="_content/blog/next-post.md"
---
title: Not ready yet
permalink: false
---
```

To publish drafts only while you write, keep them in their own folder with a folder data file that sets
`permalink: false`, and remove that file, or move the page, when it is ready. The same goes for partials that are
only included (`{% include_markdown %}`): they set `permalink: false` so they are not pages of their own.

## Pages from data

Pages can come from data files instead of Markdown: one page per product, per package, per API type. The `parse`
step reads data files as data, and a `liquid` step with a `template` renders one page per file:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  products:
    inputs: "products/*.json"
    uses: parse
    with:
      as: product
  product-pages:
    needs: products
    uses: liquid
    with:
      template: product.liquid
      permalink: "products/{{ file.data.product.slug }}/"
  write:
    needs: [markdown, product-pages]
    uses: write
```

```json title="products/lamp.json"
{ "name": "Lamp", "slug": "lamp", "price": 30 }
```

```liquid title="_includes/product.liquid"
{%- assign product = file.data.product -%}
<h1>{{ product.name }}</h1>
<p>{{ product.price }} USD</p>
```

`parse` puts the content of each file in its data, under the name of `as`. The template renders once per file, with
`file` (`file.path`, `file.data`), and site data; `permalink` is required and is rendered with `file` too. This writes
`products/lamp/index.html` and `products/desk/index.html`.

With `group-by`, the template renders once per group of files instead, with `group` (`group.key`, `group.files`):
`group-by: dir` makes one page per folder of data files. See the [liquid step](../reference/steps/liquid.md) and the
[parse step](../reference/steps/parse.md).

## See also

- [Routing and permalinks](routing.md)
- [Layouts](layouts.md)
- [Markdown](markdown.md)
- [Template data](../reference/template-data.md)
- [frontmatter step](../reference/steps/frontmatter.md)
- [liquid step](../reference/steps/liquid.md)
