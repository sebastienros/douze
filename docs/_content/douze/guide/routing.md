---
title: Routing and permalinks
description: How Douze computes the output path and the url of every page, and how to change them with permalink.
liquid: false
---

Douze has no router: the URL of a page is the path of the file it writes in the output folder. The `permalink` step
computes that path from the source path, by convention, and a page's `permalink` overrides it. This page describes the
conventions, `permalink`, and the `url` that templates use to link pages.

## Default output paths

The content folder is removed from the path, and pages get a folder of their own so that their URL ends with `/`:

| Source | Output | URL |
|---|---|---|
| `_content/index.md` | `index.html` | `/` |
| `_content/about.md` | `about/index.html` | `/about/` |
| `_content/docs/setup.html` | `docs/setup/index.html` | `/docs/setup/` |
| `_content/docs/index.md` | `docs/index.html` | `/docs/` |
| `_content/sitemap.xml.liquid` | `sitemap.xml` | `/sitemap.xml` |
| `_content/images/logo.png` | `images/logo.png` | `/images/logo.png` |

- A Markdown or HTML page `name.md` or `name.html` is written to `name/index.html`; `index.md` and `index.html` stay in
  place.
- A template `name.ext.liquid` is written to `name.ext`.
- Any other file keeps its path.

When `site.content` names another folder (`docs`), that folder is removed instead. With `site.locales`, the locale
root is removed and the locale folder is kept: `docs/ja/guide.md` is `/ja/guide/`. See
[Internationalization](internationalization.md).

## `url`

The `permalink` step sets `url` on every file: the path it is written to, from the root of the site. For a Markdown
page, it is the path of the HTML it becomes:

```liquid
{{ url }}                                    → /blog/first-post/index.html
{{ url | replace: 'index.html', '' }}        → /blog/first-post/
{{ url | replace: 'index.html', '' | absolute_url }}  → https://example.com/blog/first-post/
```

In a list of pages, each item has its data under `data`: `{{ post.data.url }}`. `collections.urls` indexes pages by
URL without `index.html` (`collections.urls["/blog/first-post/"]`), to find a page from a link.

For a site served under a path (`https://example.com/docs/`), set `site.url` to it, and write links with
`relative_url`, which prepends the path of `site.url` (`/docs/blog/first-post/`), or `absolute_url`.

## `permalink`

`permalink` in front matter sets the output path. It is a Liquid template rendered with the page's data, relative to
the root of the site, with or without a leading `/`:

```yaml
---
title: Hello World, again!
permalink: "/notes/{{ title | slug }}/"
---
```

This page is written to `notes/hello-world-again/index.html`.

- A permalink without an extension is a folder: `index.html` is added.
- A permalink with an extension is the file: `permalink: /api/posts.json`, `permalink: 404.html`.
- The `slug` filter makes a URL segment from a text: lowercase, accents removed, and anything but letters, digits, `-`, `_`
  and `~` replaced by a dash (`Café Crème` is `cafe-creme`).
- A folder data file can set `permalink` for every page of a folder; the template is rendered with each page's
  data: `permalink: "articles/{{ title | slug }}/"` in `_content/articles/articles.yml`.

A Markdown page whose permalink is not HTML (`feed.xml`) is not rendered to HTML: it is written as Liquid renders it.

## `permalink: false`

`permalink: false` leaves the file out: it is not written and not in the collections. Use it for drafts, and for
Markdown partials that other pages include:

```yaml
---
permalink: false
---
```

## Pagination URLs

A paginated page is written once per page: `blog/index.html` gives `blog/index.html`, `blog/1/index.html`,
`blog/2/index.html`... Each page has its own `url`, and `pagination.hrefs` lists them. A `permalink` is rendered for
each page, with `pagination.pageNumber` (from 0) and the page's items:

```yaml title="_content/blog/index.html"
---
pagination:
  data: collections.post
  size: 10
  alias: posts
permalink: "blog/{% if pagination.pageNumber > 0 %}page/{{ pagination.pageNumber | plus: 1 }}/{% endif %}"
---
```

This writes `blog/index.html`, `blog/page/2/index.html`, `blog/page/3/index.html`... See [Pagination](pagination.md).

## 404 pages

A `404.html` at the root of the output folder is the not found page of most hosts (GitHub Pages, Netlify,
Cloudflare Pages), and `douze serve` serves it for missing files. Since `404.md` is written to `404/index.html` by
default, give it a permalink:

```md title="_content/404.md"
---
title: Page not found
layout: base.liquid
permalink: 404.html
---
# Page not found

The page you are looking for does not exist. Go back to the [home page](/).
```

Links in a 404 page should be absolute (`/`), since it is served at any URL.

## Two files, one path

Two files written to the same path are an error, which names both sources:

```txt
error: write: /products/index.html: output '/products/index.html' is produced by both '/products/desk.json' and '/products/lamp.json'
```

It usually means a `permalink` that does not use a value that differs per page.

## See also

- [Pages](pages.md)
- [Pagination](pagination.md)
- [Customize output paths](../recipes/output-paths.md)
- [permalink step](../reference/steps/permalink.md)
- [Template data](../reference/template-data.md)
- [Liquid filters and tags](../reference/liquid.md)
