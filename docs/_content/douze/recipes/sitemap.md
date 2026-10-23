---
title: Add a sitemap
description: Generate sitemap.xml from collections.all with a Liquid template, leave pages out, and point robots.txt to it.
---

A sitemap is a template over `collections.all`, the list of every page of the site. `sitemap.xml.liquid` in the content
folder is rendered like a page and written to `sitemap.xml`. Templates that are not pages, such as the sitemap itself
or a feed, are not in the collections.

## Set the site URL

The URLs of a sitemap are absolute. Set `site.url`, which the `absolute_url` filter prefixes:

```yaml title="douze.yml"
site:
  url: https://example.com
```

## Write the sitemap

```liquid
<?xml version="1.0" encoding="utf-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
{%- assign pages = collections.all | where_exp: "p", "p.data.sitemap != false" %}
{%- for p in pages %}
  <url>
    <loc>{{ p.data.url | replace: 'index.html', '' | absolute_url }}</loc>
    <lastmod>{{ p.data.page.date | date_to_xmlschema | raw }}</lastmod>
  </url>
{%- endfor %}
</urlset>
```

Save it as `_content/sitemap.xml.liquid`. The build writes `_site/sitemap.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url>
    <loc>https://example.com/</loc>
    <lastmod>2026-10-23T16:32:02+00:00</lastmod>
  </url>
  <url>
    <loc>https://example.com/docs/a/</loc>
    <lastmod>2026-10-23T16:32:02+00:00</lastmod>
  </url>
</urlset>
```

- `url` is the output path of a page (`/docs/a/index.html`); removing `index.html` gives the folder URL.
- `page.date` is the modification time of the source file. In a CI checkout every file has the time of the checkout:
  [Add last modified time](modified-time.md) sets it from Git instead. Use `p.data.date` to list the front matter
  date of the pages that have one.
- The `liquid` step encodes its output for HTML, which writes the `+` of a time offset as `&#x2B;`. `| raw` writes
  the date as it is.

## Leave pages out

The `where_exp` filter keeps the pages for which the expression is true. A page that should not be listed, such as a
404 page or a draft, sets `sitemap: false` in its front matter:

```html
---
permalink: /404.html
sitemap: false
---
<h1>Not found</h1>
```

A data file of a folder can set it for every page of the folder:

```yaml title="_content/drafts/drafts.yml"
sitemap: false
```

## Point robots.txt to it

`robots.txt.liquid` is rendered and written to `robots.txt`:

```liquid
User-agent: *
Allow: /

Sitemap: {{ '/sitemap.xml' | absolute_url }}
```

## Multilingual sites

On a site with `site.locales` and the `localize` step, the `locale_pages` filter lists every page of every locale,
fallback copies included, as `{ locale, lang, url, fallback, data }`. See
[Internationalization](../guide/internationalization.md).

## See also

- [Feeds, sitemaps and other outputs](../guide/outputs.md)
- [Collections](../guide/collections.md)
- [Add an RSS feed](rss.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Template data](../reference/template-data.md)
