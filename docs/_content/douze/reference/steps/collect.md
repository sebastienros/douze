---
title: collect
description: "Builds collections.all, collections.<tag> and collections.urls from the pages."
---
The `collect` step builds the collections of a site from the pages it consumes: `collections.all`, one
`collections.<tag>` per tag, and `collections.urls`, the same pages by url. It emits no files: the pages it consumes
go on through the other steps that need the step before it, and the collections are site data for the steps that
need it.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.html`, `**/*.md` |
| Default workflow | yes, as `collections` |

## What it does

The step waits for every page it consumes, then publishes one site value, `collections` (or `name`):

| Value | Contains |
|---|---|
| `collections.all` | every page |
| `collections.<tag>` | the pages whose `tags` contain the tag |
| `collections.urls` | every page by its url without a trailing `index.html`: `collections.urls["/blog/post/"]` |

Each item is an object with `data`, the page's data at that point of the workflow (`item.data.title`,
`item.data.url`), and with `content: true`, `content`, the page's text at that point. At its root, it also has the
values that filters taking a member name (`sort`, `where`, `map`) need, since they don't read paths like `data.date`:

| Member | Value |
| --- | --- |
| `date` | the date the items are sorted by: front matter `date`, else `page.date`; absent when neither is a date |
| `url` | the page's `url`, the same as `data.url` (with `index.html`) |
| `inputPath` | the source path of the page (`/posts/first.md`), its own path for a file that a step made |

- `tags` is a string or a list, read from the page's data, its folder data or the site data. Empty tags are ignored,
  and a tag listed twice adds the page once.
- Items are sorted by date, then by source path (ordinal). The date is `date` if the page defines it (a date, or a
  string that parses as one), else `page.date`, the modification time of the file set by
  [`frontmatter`](frontmatter.md). Pages without either come first.
- In `collections.urls`, when two pages have the same url, the first one of `collections.all` wins. Pages without a
  `url` (before [`permalink`](permalink.md)) are not in it.

Because the files the step consumes do not come out of it, a step that needs the pages and the collections needs
both: `needs: [permalink, collections]`.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `name` | string | `collections` | The name of the site value, so that a second `collect` step does not hide the first. Must not be empty |
| `content` | boolean | `false` | Items also have `content`, the text of the page at that point of the workflow (`null` for a binary file) |

## Caching

Without `content`, the step reads the data of the pages only: editing the body of a page does not build the
collections again, nor render the pages that list them. With `content: true`, it runs again when a body changes, and
holds every body in memory.

A page that reads the collections depends on what it read of them: editing the title of a post renders again the
pages that list the title.

## Errors

```
douze.yml(5,3): error: option 'name' of step 'docs-index' must not be empty
```

## Example

A second collection, of the HTML of the documentation pages after Markdown, for a search index:

```yaml
steps:
  pages:
    inputs: "**/*.{md,liquid}"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  markdown:
    needs: permalink
    uses: markdown
  docs-index:
    needs: markdown
    uses: collect
    match: "docs/**/*.html"
    with:
      name: docs
      content: true
  render:
    needs: [markdown, docs-index]
    uses: liquid
  write:
    needs: render
    uses: write
```

`search.txt.liquid`:

```liquid
{% for d in docs.all %}{{ d.data.title }}: {{ d.content | strip_html | strip }}
{% endfor %}
```

## Related

- [Collections](../../guide/collections.md)
- [paginate](paginate.md): `pagination.data: collections.post`
- [localize](localize.md): reads `collections.urls`
- [Template data](../template-data.md)
