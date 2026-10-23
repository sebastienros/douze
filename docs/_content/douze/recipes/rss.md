---
title: Add an RSS feed
description: Publish an RSS 2.0 feed of a collection with a feed.xml.liquid template, and optionally the full HTML of each post.
liquid: false
---

A feed is a template: `feed.xml.liquid` in the content folder is rendered like a page and written to `feed.xml`. It
lists the pages of a collection, newest first. Feeds are not in the collections themselves, and layouts only apply to
HTML files, so the template writes the whole document.

## Tag the posts

Give every post a tag, here `post`, and a `date`. A data file in the posts folder sets the tag and the layout for
every page in it:

```yaml title="_content/blog/blog.yml"
layout: post.liquid
tags: post
```

```md
---
title: First post
date: 2026-01-10T09:00:00Z
description: What this blog is about.
---
Hello **world**.
```

:::tip[Dates without a time]
A `date` without a time, such as `2026-01-10`, is midnight in the time zone of the machine that builds the site, so
the feed of a CI build can differ from a local one. Give a time and an offset (`2026-01-10T09:00:00Z`) when it
matters.
:::

## Set the site URL

Feed readers need absolute links. Set `site.url` in `douze.yml`; the `absolute_url` filter prefixes it:

```yaml title="douze.yml"
site:
  url: https://example.com
```

The title and description of the feed can come from a data file, `_data/site.json`, which is merged with the `site`
section:

```json
{ "title": "My blog", "description": "Notes about things" }
```

## Write the feed

```liquid title="_content/feed.xml.liquid"
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
  <channel>
    <title>{{ site.title }}</title>
    <link>{{ '/' | absolute_url }}</link>
    <description>{{ site.description }}</description>
    <atom:link href="{{ '/feed.xml' | absolute_url }}" rel="self" type="application/rss+xml"/>
    {%- assign posts = collections.post | reverse %}
    {%- if posts.size > 0 %}
    <lastBuildDate>{{ posts.first.data.date | date_to_rfc822 }}</lastBuildDate>
    {%- endif %}
    {%- for post in posts limit: 20 %}
    <item>
      <title>{{ post.data.title }}</title>
      <link>{{ post.data.url | replace: 'index.html', '' | absolute_url }}</link>
      <guid>{{ post.data.url | replace: 'index.html', '' | absolute_url }}</guid>
      <pubDate>{{ post.data.date | date_to_rfc822 }}</pubDate>
      {%- if post.data.description %}
      <description>{{ post.data.description }}</description>
      {%- endif %}
    </item>
    {%- endfor %}
  </channel>
</rss>
```

The build writes `_site/feed.xml`:

```xml
<item>
  <title>First post</title>
  <link>https://example.com/blog/first/</link>
  <guid>https://example.com/blog/first/</guid>
  <pubDate>Sat, 10 Jan 2026 09:00:00 +0000</pubDate>
  <description>What this blog is about.</description>
</item>
```

Notes on the template:

- Collections are sorted by date ascending, so `reverse` puts the newest post first.
- `url` is the output path (`/blog/first/index.html`): `replace: 'index.html', ''` turns it into the folder URL.
- The `liquid` step encodes the outputs of an XML file for XML: `{{ post.data.title }}` writes `Second &lt;post&gt;`,
  and the dates keep their `+0000`. `xml_escape` is not needed, and not encoded again where it is used.

Point to the feed from the layout of the pages:

```liquid
<link rel="alternate" type="application/rss+xml" title="{{ site.title }}" href="{{ '/feed.xml' | absolute_url }}">
```

## Include the full content

The items of `collections.post` have the data of each post, not its HTML: the default `collections` step runs before
Liquid and Markdown. To put the rendered posts in the feed, add a second `collect` step after `markdown` with
`content: true`, and render the feed after it. This replaces the default workflow, so `douze.yml` lists every step:

```yaml title="douze.yml"
site:
  url: https://example.com

steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
    match: ["**/*.{html,md,json,xml,txt}", "!feed.xml"]
  markdown:
    needs: render
    uses: markdown
  # The posts after Markdown, with their HTML, as feed.all and feed.<tag>
  feed-posts:
    needs: markdown
    uses: collect
    match: "blog/**/*.html"
    with:
      name: feed
      content: true
  feed:
    needs: [markdown, feed-posts]
    uses: liquid
    match: feed.xml
  layout:
    needs: feed
    uses: layout
  write:
    needs: layout
    uses: write
```

Steps after `permalink` see output paths: the feed is `feed.xml`, and the posts are `blog/first/index.md` before the
`markdown` step and `blog/first/index.html` after it. `render` leaves the feed alone, and `feed` renders it once
`feed-posts` has collected every post. In the template, read `feed.post` instead of `collections.post`, and write
`content`, which the encoder escapes as RSS expects:

```liquid
{%- assign posts = feed.post | reverse %}
{%- for post in posts limit: 20 %}
<item>
  <title>{{ post.data.title }}</title>
  <link>{{ post.data.url | replace: 'index.html', '' | absolute_url }}</link>
  <pubDate>{{ post.data.date | date_to_rfc822 }}</pubDate>
  <description>{{ post.content }}</description>
</item>
{%- endfor %}
```

`content` is the HTML of the post before its layout. With `content: true`, the collect step holds every post's HTML in
memory and runs again when any post changes.

## See also

- [Feeds, sitemaps and other outputs](../guide/outputs.md)
- [Collections](../guide/collections.md)
- [Add a sitemap](sitemap.md)
- [collect step](../reference/steps/collect.md)
- [liquid step](../reference/steps/liquid.md)
- [Liquid filters and tags](../reference/liquid.md)
