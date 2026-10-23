---
title: Collections
description: Group pages with tags, list them with collections.all and collections.<tag>, find them by url, and sort, filter and group them.
liquid: false
---

A collection is a list of pages that templates can loop over: every page, or the pages that share a tag. The
`collect` step builds them once every page has its data, and the pages rendered after it read them as
`collections`. This page covers tags, the collections Douze builds, their order, the `collect` step's options and the
filters that sort, filter and group them.

## Tags

A page joins a collection through `tags` in its front matter, a string or a list:

```yaml title="_content/blog/second.md"
---
title: Second
date: 2026-03-02
tags: [post, news]
---
```

`tags` resolves like any other name, so a folder data file can tag every page of its folder:

```yaml title="_content/blog/blog.yml"
tags: post
```

A page's own `tags` replace the folder's: `second.md` above is in `post` and `news` because it lists both.

## The collections

| Name | Holds |
|---|---|
| `collections.all` | every page (`.md` and `.html`) that reaches the `collect` step |
| `collections.<tag>` | the pages that have the tag |
| `collections.urls` | the same pages by url, without `index.html`: `collections.urls["/blog/first/"]` |

Each item is `{ data, date, url, inputPath }`, where `data` is the page's data (`post.data.title`,
`post.data.page.date`), `date` the date the collections are sorted by (front matter `date`, else `page.date`), `url`
the page's url (with `index.html`) and `inputPath` its source path (`/posts/first.md`). Liquid filters take a member
name, not a path like `data.date`, so filter and sort on these: `collections.post | sort: 'date' | reverse`,
`collections.all | where: 'url', url`.
Items do not hold the page's content (see [Content in collections](#content-in-collections)).

```liquid title="_content/index.html"
<ul>
{%- for post in collections.post %}
  <li><a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a></li>
{%- endfor %}
</ul>
```

`collections.urls` finds a page from a link, for a sidebar or a "related pages" list:

```liquid
{{ collections.urls["/blog/first/"].data.title }}
```

Templates such as `feed.xml.liquid` and `sitemap.xml.liquid` are not in the collections, and a paginated page is in
`collections.all` once, with the url of its first page.

## Order

Items are sorted by date, oldest first: the front matter `date` when the page has one, else `page.date`, the file's
modification time. Pages with the same date are sorted by source path. The order is the same on every build, whatever
the parallelism, and matches Eleventy's.

`page.date` changes when a file is checked out or copied, so a page without a `date` can move in a list from one
machine to another. Give dated content a `date`.

To list the newest first, sort in the template:

```liquid
{% assign posts = collections.post | sort_by: "data.date", "desc" %}
```

## Filters

Fluid has Liquid's standard filters (`sort`, `reverse`, `first`, `last`, `size`...), but `where` compares a top-level
member of each item, and the members of a page item are under `data`. Douze adds filters that take a path or an
expression, for lists of pages:

| Filter | Result |
|---|---|
| `where_exp: "item", "item.data.draft != true"` | the items for which the expression is true |
| `sort_by: "data.title"`, `sort_by: "data.date", "desc"` | the items sorted by the value at a path, missing values last |
| `group_by_exp: "item", "item.data.date \| date: '%Y'"` | `{ name, items, size }` per value, in order of appearance |
| `index_by: "data.url"` | an object of the items by the value at a path |

Filters are applied in an `assign`, then the result is looped over: a `for` tag takes a variable, not a filter chain.

```liquid title="_content/index.html"
{%- assign posts = collections.post | where_exp: "item", "item.data.draft != true" | sort_by: "data.date", "desc" %}
{%- for post in posts %}
  <a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a> {{ post.data.date | date: "%Y-%m-%d" }}
{%- endfor %}

{%- assign years = collections.post | group_by_exp: "item", "item.data.date | date: '%Y'" %}
{%- for year in years %}
  <h2>{{ year.name }}</h2> {{ year.size }} posts
{%- endfor %}
```

## The collect step

The default workflow runs `collect` after `permalink`, so items have their `url`, and before `paginate` and `render`,
so pages can list and paginate them:

```yaml
collections:
  needs: permalink
  uses: collect
```

Its `match` is `**/*.html` and `**/*.md` by default. Its options:

| Option | Default | Meaning |
|---|---|---|
| `name` | `collections` | the name templates read, so that a second `collect` step does not hide the first |
| `content` | `false` | items also have `content`, the text of the page at that point of the workflow |

### Content in collections

By default, editing the body of a page does not build the collections again, and so does not render the pages that
list it, only the page itself. A page's title, tags or date do. A feed that shows the content of each post needs
`content: true` on a second step, placed after `markdown` so that `content` is HTML:

```yaml title="douze.yml"
posts:
  needs: markdown
  uses: collect
  match: "blog/**/*.html"
  with:
    name: posts
    content: true
```

```liquid
{% for post in posts.all %}{{ post.content | raw }}{% endfor %}
```

`content` is a string, which Liquid HTML-encodes on output like any other: write it with `raw`. That step runs again whenever a body changes, and holds every body in memory until the build ends. Make the steps
that read `posts` need it.

## Collections and the cache

A page that lists a collection depends on what it reads: adding a page, or changing a title, tag or date of a listed
page, renders it again. See [Cache and incremental builds](cache.md).

## See also

- [collect step reference](../reference/steps/collect.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Template data](../reference/template-data.md)
- [Pagination](pagination.md)
- [Data files](data.md)
