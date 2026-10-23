---
title: Pagination
description: Split a list or a collection over several pages, generate a page per item, and link the pages together.
liquid: false
---

Pagination turns one template into several pages: a blog index of ten posts per page, or one page per product of a
data file. A page declares what it paginates in its `pagination` front matter, and the `paginate` step writes one
page per slice. This page covers the options, the variables each page gets, permalinks, navigation, and the limits.

## Paginate a list

```yaml
---
pagination:
  data: products     # a name, or a dotted path such as collections.post or site.products
  size: 2            # items per page
  alias: items       # optional: a name for the items of the page
  resolve: values    # optional, for an object: paginate its values instead of its keys
---
```

`data` names any value the page can resolve: its own front matter, folder data, site data or collections. A list is
paginated item by item. An object is paginated by key, or by value with `resolve: values`.

## Paginate a collection

The default workflow builds collections before pagination, so a page can paginate `collections.<tag>`:

```html title="_content/blog/index.html"
---
pagination:
  data: collections.post
  size: 2
  alias: posts
---
<h1>Page {{ pagination.pageNumber | plus: 1 }} of {{ pagination.pages.size }}</h1>
{%- for post in posts %}
<a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a>
{%- endfor %}
```

With five posts, this writes `blog/index.html`, `blog/1/index.html` and `blog/2/index.html`. The posts come in the
order of the collection: oldest first (see [Collections](collections.md#order)). `pagination.data` takes a name, not
a filter chain, so a page cannot sort or filter what it paginates.

## What each page gets

| Variable | Value |
|---|---|
| `pagination.items` | the items of this page |
| `pagination.pageNumber` | the index of this page, from 0 |
| `pagination.pages` | every page, each `{ items, pageNumber, href }` |
| `pagination.hrefs` | the path of every page |
| `pagination.page.next`, `previous`, `first`, `last` | those pages, `{ items, pageNumber, href }`; `next` and `previous` are nil at the ends |
| the alias | the items of this page; with `size: 1`, the item itself |
| `url` | this page's url |

An `href` is the path of the page's file, such as `/blog/1/index.html`. Remove `index.html` for clean links.

## Navigation

```html title="_content/blog/index.html"
<nav>
{%- if pagination.page.previous %}<a href="{{ pagination.page.previous.href | replace: 'index.html', '' }}">Previous</a>{% endif %}
{%- for p in pagination.pages %}
  <a href="{{ p.href | replace: 'index.html', '' }}"{% if p.pageNumber == pagination.pageNumber %} aria-current="page"{% endif %}>{{ p.pageNumber | plus: 1 }}</a>
{%- endfor %}
{%- if pagination.page.next %}<a href="{{ pagination.page.next.href | replace: 'index.html', '' }}">Next</a>{% endif %}
</nav>
```

Pagination variables only link the pages generated from the same template.

## Permalinks

Without a `permalink`, the first page keeps the template's path and the others get a numbered folder:
`products/index.html`, `products/1/index.html`, `products/2/index.html`. A `permalink` is rendered for each page,
with its `pagination` and its alias, so it can name pages after their content:

```yaml title="_data/products.yml"
- name: Widget
  slug: widget
- name: Gadget
  slug: gadget
```

```html title="_content/product.html"
---
pagination:
  data: products
  size: 1
  alias: product
permalink: "products/{{ product.slug }}/"
---
<h1>{{ product.name }}</h1>
```

This writes `products/widget/index.html` and `products/gadget/index.html`. A permalink can use
`pagination.pageNumber` (`"blog/page-{{ pagination.pageNumber | plus: 1 }}/"`) but not the `href` values, which come
from the permalinks. A permalink that renders the same path for every page is ignored, and the pages get numbered
folders.

## Paginate an object

```html title="_content/colors.html"
---
pagination:
  data: colors
  size: 1
  alias: color
  resolve: values
permalink: "colors/{{ pagination.pageNumber }}.html"
colors:
  red: "#f00"
  green: "#0f0"
---
{{ color }}
```

Without `resolve: values`, the items are the keys (`red`, `green`).

## Limits

- A list with no items gives no page at all: the template is not written.
- The paginated page is in `collections.all` once, with the url of its first page.
- `pagination` has no `reverse`, `filter` or `before` option. Sort or filter the data where it is made, or keep a
  data file in the order you want.

## See also

- [paginate step reference](../reference/steps/paginate.md)
- [Collections](collections.md)
- [Routing and permalinks](routing.md)
- [Data files](data.md)
- [Template data](../reference/template-data.md)
