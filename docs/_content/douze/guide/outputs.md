---
title: Feeds, sitemaps and other outputs
description: Generate RSS feeds, sitemaps, llms.txt, robots.txt and JSON files from templates.
liquid: false
---

Not every file of a site is a page. Feeds, sitemaps, `robots.txt`, `llms.txt` and JSON files are generated from the
site's data at build time. In Douze they are templates in the content folder: `feed.xml.liquid` is rendered like a
page and written as `feed.xml`. This page shows the common ones and the filters they use.

## How output templates work

A file named `name.ext.liquid` in the content folder:

- has front matter, like a page, and is rendered as Liquid with the page's data, `site`, site data and `collections`;
- is written to `name.ext`, or to its `permalink`;
- is not in the collections, and gets no layout: layouts only apply to HTML files.

`.txt` files of the content folder are rendered as Liquid too, so `robots.txt` needs no `.liquid` extension. `.json`,
`.xml`, `.yml` and `.yaml` files are data files of their folder and are not published: write `api.json.liquid` or
`feed.xml.liquid`, not `api.json` or `feed.xml`.

:::note[Output encoding]
The `liquid` step encodes the output of `{{ }}` for the file it writes: XML entities in `feed.xml` and `sitemap.xml`
(`&`, `<`, `>`, `"` and `'`), JSON string escapes in `.json` files, and nothing in text files such as `robots.txt` and
`llms.txt`. A feed's titles and a JSON file's strings need no filter, and `xml_escape` or `json` are not encoded
again. See [output encoding](../reference/steps/liquid.md#output-encoding).
:::

## RSS feed

```liquid title="_content/feed.xml.liquid"
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0">
<channel>
  <title>My blog</title>
  <link>{{ "/" | absolute_url }}</link>
  <description>Posts of my blog</description>
  {%- assign posts = collections.post | sort_by: "data.date", "desc" %}
  {%- for post in posts limit: 20 %}
  <item>
    <title>{{ post.data.title }}</title>
    <link>{{ post.data.url | replace: 'index.html', '' | absolute_url }}</link>
    <guid>{{ post.data.url | replace: 'index.html', '' | absolute_url }}</guid>
    <pubDate>{{ post.data.date | date_to_rfc822 }}</pubDate>
    <description>{{ post.data.description }}</description>
  </item>
  {%- endfor %}
</channel>
</rss>
```

- `collections.post` holds the pages tagged `post`, sorted by date ascending; `sort_by: "data.date", "desc"` puts the
  newest first.
- `absolute_url` prepends `site.url`, set in `douze.yml`.
- A date without a time in front matter (`date: 2026-01-02`) is midnight in the time zone of the machine that builds,
  so the feed differs between machines. Write dates with a time zone (`date: 2026-01-02T00:00:00Z`) for the same
  output everywhere.

The items of `collections` have the data of the pages, not their content. For full content in a feed, add a second
`collect` step with `content: true`. See [Add an RSS feed](../recipes/rss.md) and the
[collect step](../reference/steps/collect.md).

## Sitemap

```liquid title="_content/sitemap.xml.liquid"
<?xml version="1.0" encoding="utf-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
{%- for p in collections.all %}
  <url><loc>{{ p.data.url | replace: 'index.html', '' | absolute_url }}</loc><lastmod>{{ p.data.page.date | date_to_xmlschema }}</lastmod></url>
{%- endfor %}
</urlset>
```

`page.date` is the file's modification time, and `date` the front matter date when the page has one. On a
multilingual site, `collections.all | locale_pages` lists the pages of every locale, fallback copies included. See
[Add a sitemap](../recipes/sitemap.md).

## `robots.txt`

```txt title="_content/robots.txt"
User-agent: *
Sitemap: {{ "/sitemap.xml" | absolute_url }}
```

## `llms.txt`

```liquid title="_content/llms.txt.liquid"
# My site

> Documentation of My product.

{% for p in collections.all %}
- [{{ p.data.title }}]({{ p.data.url | replace: 'index.html', '' | absolute_url }}): {{ p.data.description }}
{%- endfor %}
```

See [Build with AI](build-with-ai.md) for Markdown versions of every page.

## JSON

```liquid title="_content/posts.json.liquid"
---
permalink: /api/posts.json
---
[
{%- for post in collections.post %}
  { "title": {{ post.data.title | json }}, "url": "{{ post.data.url | replace: 'index.html', '' }}" }{% unless forloop.last %},{% endunless %}
{%- endfor %}
]
```

`json` (or `jsonify`) writes a value as JSON, with its quotes and escapes. A whole object or list works too:
`{{ site.authors | json }}`. Inside quotes, `{{ }}` writes the content of a JSON string: `"{{ post.data.url }}"`.

For many JSON files generated from data, rather than one template per file, use a `liquid` step with a `template`
whose `permalink` ends in `.json`: its outputs are encoded for JSON too. See [Pages from data](pages.md#pages-from-data)
and the [liquid step](../reference/steps/liquid.md).

## Filters for outputs

| Filter | Result |
|---|---|
| `absolute_url` | `site.url` followed by the url |
| `relative_url` | The url after the path of `site.url` (`/blog/x/` for `https://example.com/blog`) |
| `xml_escape` | `&`, `<`, `>`, `"` and `'` as entities, for a step with `encode: none` |
| `strip_html` | The text without tags, comments, scripts and styles |
| `date_to_xmlschema` | `2020-01-02T03:04:05+00:00` |
| `date_to_rfc822` | `Thu, 02 Jan 2020 03:04:05 +0000` |
| `sort_by: "data.date", "desc"` | The items sorted by the value at a path, missing values last |
| `where_exp: "item", "item.data.draft != true"` | The items for which the expression is true |
| `json`, `jsonify` | The value as JSON |
| `excerpt` | The first paragraph of HTML, or what comes before `<!-- more -->` |

See [Liquid filters and tags](../reference/liquid.md) for every filter.

## See also

- [Add an RSS feed](../recipes/rss.md)
- [Add a sitemap](../recipes/sitemap.md)
- [Routing and permalinks](routing.md)
- [Collections](collections.md)
- [Liquid filters and tags](../reference/liquid.md)
