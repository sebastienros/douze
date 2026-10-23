---
title: permalink
description: "Moves each file to its output path and sets its url."
liquid: false
---
The `permalink` step moves each file to its output path and sets its `url`. Pages get folder URLs
(`blog/post.md` is written to `blog/post/index.html`), templates such as `sitemap.xml.liquid` lose their `.liquid`
extension, a `permalink` value overrides the path, and `permalink: false` drops the file.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | every file (`**`) |
| Default workflow | yes, as `permalink`, with `root` set to the content folder when it is not the source folder |

## What it does

For each file, the step computes the output path, removes `root` from its start, moves the file there and sets `url`.

### Default paths

Without a `permalink` value:

| Source (under `root`) | Output path | `url` |
|---|---|---|
| `index.html`, `index.md` | `/index.html`, `/index.md` | `/index.html` |
| `blog/post.html` | `/blog/post/index.html` | `/blog/post/index.html` |
| `blog/post.md` | `/blog/post/index.md` | `/blog/post/index.html` |
| `sitemap.xml.liquid` | `/sitemap.xml` | `/sitemap.xml` |
| `feed.liquid` | `/feed.liquid` | `/feed.liquid` |
| `images/logo.png` | `/images/logo.png` | `/images/logo.png` |

A Markdown page keeps its `.md` extension until the [`markdown`](markdown.md) step renders it and
renames it to `.html`, so that the steps between them still match it as Markdown. Its `url` is already the HTML path.

### The permalink value

`permalink` is read like any other name: from the file's front matter, or from folder or site data. It is a Liquid
template rendered with the file's data, and the result is a path from the root of the output folder:

| `permalink` | Output path |
|---|---|
| `/about.html` | `/about.html` |
| `guide/start` (no extension: a folder) | `/guide/start/index.html` |
| `/feed.xml` | `/feed.xml` |
| `/blog/{{ slug }}/` (a template) | `/blog/hello/index.html`, with `slug: hello` |
| `false` | the file is dropped: it is not written |
| empty | the default path |

A template can use filters, for a dated path built from the title:

```yaml
---
title: Hello World
date: 2024-05-01
permalink: "/blog/{{ date | date: '%Y' }}/{{ title | slug }}/"
---
```

This page is written to `/blog/2024/hello-world/index.html`.

A Markdown page whose `permalink` is HTML keeps the `.md` extension until the `markdown` step, as above. A Markdown
page whose `permalink` is not HTML (`/feed.xml`) is not renamed, and the `markdown` step does not render it.

`root` is removed from the start of the rendered path too: with `root: docs`, `permalink: docs/start` is
`/start/index.html`, like `permalink: start`.

### Locales

With [`site.locales`](../configuration.md#sitelocales), a file under the locale root (`site.locales.root`) has that
root removed instead of `root`, when the locale root is inside `root`: `docs/ja/guide.md` with `root: docs` is
`/ja/guide/index.md`.

### Paginated files

A file whose `pagination` is an object gets its default path, without its `permalink` being rendered: its permalink
reads the items of each page, which the [`paginate`](paginate.md) step computes later.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `root` | folder | the content folder | The folder removed from the start of output paths (`_content/blog/a.md` is `/blog/a/index.md`). Paths outside it are kept as they are |

## Caching

The result of a file depends on the `permalink` value it resolved and on the data its template read.

## Example

```yaml
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
  markdown:
    needs: render
    uses: markdown
  write:
    needs: markdown
    uses: write
```

`docs/guide/install.md` is written to `guide/install/index.html`, with `url` `/guide/install/index.html`.

## Related

- [Routing and permalinks](../../guide/routing.md)
- [Customize output paths](../../recipes/output-paths.md)
- [paginate](paginate.md)
- [fork](fork.md): writes a copy of a file to another path
- [Template data](../template-data.md): `url`
