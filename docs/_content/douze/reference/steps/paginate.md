---
title: paginate
description: "Expands a page whose pagination names a list into one page per slice of the list."
liquid: false
---
The `paginate` step expands a page whose `pagination` names a list into one page per slice of the list, each with its
own items, path and `url`. It runs after [`permalink`](permalink.md) and
[`collect`](collect.md), so a page can paginate a collection. Files without `pagination` pass through.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html`, `**/*.md`, `**/*.json`, `**/*.liquid`, `**/*.xml`, `**/*.txt` |
| Default workflow | yes, as `paginate`, after `permalink` and `collections` |

## What it does

A page is paginated when its `pagination` (front matter, folder or site data) is an object with a non-empty `data`
and a `size`:

```yaml
---
pagination:
  data: products     # a name, or a dotted path into one: collections.post, site.products
  size: 2            # items per page, a positive integer
  alias: items       # optional: a name for the items of the page
  resolve: values    # optional, for objects: paginate the values instead of the keys
products: [a, b, c]
---
```

| Key | Meaning |
|---|---|
| `data` | The list to paginate. The first segment resolves like any name (the page's data, folder data, site data, `collections`), the others are keys of it. An object paginates its keys, or its values with `resolve: values`. When the name does not resolve, the page passes through unchanged |
| `size` | The number of items per page. Not a positive integer: `'pagination.size' must be a positive integer` |
| `alias` | A name for the items of the page: the item itself when `size` is 1, the list of items otherwise |
| `resolve` | `values` to paginate the values of an object instead of its keys |

Each page has its own `pagination`:

| Value | Meaning |
|---|---|
| `pagination.items` | the items of the page |
| `pagination.pageNumber` | the index of the page, from 0 |
| `pagination.hrefs` | the url of every page |
| `pagination.pages` | every page, each `{ items, pageNumber, href }` |
| `pagination.page.next`, `previous`, `first`, `last` | the neighbor, first and last pages, each `{ items, pageNumber, href }`; `next` and `previous` are `nil` at the ends |

### Paths

The `permalink` of the page is rendered once per page, with that page's `pagination` and alias, so it can use them
(`/tags/{{ tag }}/`, or `pagination.pageNumber`). It cannot use the `href` values, which come from it. A page whose
`permalink` renders `false` is dropped.

When every page resolves to the same path (no `permalink`, or one that does not depend on the page), pages get
numbered paths: `products.html` is written to `products/index.html`, `products/1/index.html`,
`products/2/index.html`... (`index.md` for Markdown).

Each page gets its path as `permalink` and its `url`, as with the `permalink` step. `root` is removed from the start
of the paths as `permalink` does. An empty list gives no page: nothing is written for the template.

The paginated page is in `collections.all` once, with the `url` of its first page, since collections are built
before pagination.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `root` | folder | the content folder | The folder removed from the start of the paths, as for [`permalink`](permalink.md) |

## Errors

```
error: paginate: /_content/p.html: 'pagination.size' must be a positive integer
error: paginate: /p/index.html: 'pagination.data' of '/_content/p.html' must name a list or an object, 'title' is neither
```

## Example

One page per post:

```yaml
steps:
  pages:
    inputs: "**/*.{md,html}"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  write:
    needs: markdown
    uses: write
```

`posts.html`:

```liquid
---
pagination:
  data: collections.post
  size: 1
  alias: post
---
<h1>{{ post.data.title }}</h1>
{% if pagination.page.next %}<a href="{{ pagination.page.next.href }}">Next</a>{% endif %}
```

With two posts tagged `post`, this writes `posts/index.html` and `posts/1/index.html`.

## Related

- [Pagination](../../guide/pagination.md)
- [collect](collect.md)
- [permalink](permalink.md)
- [Template data](../template-data.md): `pagination.*`
