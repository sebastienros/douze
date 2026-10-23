---
title: "Unit 5: Data and collections"
description: Use data files for site settings and navigation, list posts with collections, generate tag pages with pagination, an RSS feed and a sitemap.
---

In this unit you will:

- move the site's settings and its navigation to data files,
- list the posts with `collections.post`, newest first, on a blog page and on the home page,
- generate a page per topic with pagination,
- generate an RSS feed and a sitemap from templates.

## Step 1: add the site's settings

The title "My blog" and the author "Ada" are written in several files. Move them to a data file: create
`_data/site.yml`:

```yaml title="_data/site.yml"
title: Ada's blog
description: Notes from a web developer who is learning Douze.
author: Ada
url: https://example.com
```

{% raw %}
Like `_data/layout.yml` in unit 4, the file is a variable named after it: `site`, so templates read
`{{ site.title }}` and `{{ site.description }}`. `site.url` is the address where the site is published: the feed and
the sitemap need it to write absolute links. Replace `https://example.com` with yours when you deploy in unit 6.
{% endraw %}

## Step 2: build the navigation from data

Create `_data/navigation.yml`, a list of links:

```yaml title="_data/navigation.yml"
- label: Home
  url: /
- label: Blog
  url: /blog/
- label: About
  url: /about/
- label: Projects
  url: /projects/
```

Then replace the header and the footer with versions that read the data files:

```html title="_includes/header.html"
{%- assign current = url | replace: "index.html", "" -%}
<header>
  <a class="site-title" href="/">{{ site.title }}</a>
  <nav>
    {%- for item in navigation %}
    <a href="{{ item.url }}"{% if item.url == current %} aria-current="page"{% endif %}>{{ item.label }}</a>
    {%- endfor %}
  </nav>
</header>
```

```html title="_includes/footer.html"
<footer>
  <p>Written by {{ site.author }}. This page changed on {{ page.date | date: "%B %-d, %Y" }}.</p>
  <p><a href="/feed.xml">RSS feed</a></p>
</footer>
```

{% raw %}
`url` is a variable of every page: its output path, such as `/about/index.html`. The header removes `index.html` to
compare it with the links, and marks the link of the current page with `aria-current="page"`.
{% endraw %}

Add the site's title and description to the base layout, and a link to the feed you will create in step 6:

```html title="_includes/base.html" ins={6-8}
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{ title }} | {{ site.title }}</title>
    <meta name="description" content="{{ description | default: site.description }}">
    <link rel="alternate" type="application/rss+xml" title="{{ site.title }}" href="/feed.xml">
    <link rel="stylesheet" href="/styles/global.css">
  </head>
  <body>
    {% include "header.html" %}
    <main>
      {{ content }}
    </main>
    {% include "footer.html" %}
  </body>
</html>
```

Every page now has the title of the site in its tab, and a Blog link in its header. The Blog page does not exist yet:
that is the next step.

:::note[Escaping]
{% raw %}
Douze escapes what `{{ }}` outputs for HTML: the `'` of "Ada's blog" is written `&#x27;`. The HTML of a page in
`{{ content }}` is not escaped.
{% endraw %}
:::

## Step 3: list the posts on a blog page

Remember the `tags` of the posts? Douze groups the pages by tag in **collections**: `collections.post` is the list of
every page tagged `post`, sorted by `date`, oldest first. Create `_content/blog.md`:

```md title="_content/blog.md"
---
title: Blog
---
# {{ title }}

All my posts, newest first.

{% assign posts = collections.post | reverse %}
<ul class="post-list">
{%- for post in posts %}
  <li>
    <a href="{{ post.data.url | replace: "index.html", "" }}">{{ post.data.title }}</a>
    <time datetime="{{ post.data.date | date: "%Y-%m-%d" }}">{{ post.data.date | date: "%B %-d, %Y" }}</time>
  </li>
{%- endfor %}
</ul>

## Topics

{% for topic in topics %}
- [{{ topic.name }}](/tags/{{ topic.name }}/): {{ topic.description }}
{%- endfor %}
```

{% raw %}
Each item of a collection has the page's variables in `data`: `post.data.title`, `post.data.date`, `post.data.url`.
The `reverse` filter puts the newest post first.
{% endraw %}

Open `http://localhost:8080/blog/`: the three posts are listed, the newest first. Add a fourth post to
`_content/posts/` and it shows up in the list as soon as you save it.

The Topics section reads `topics`, a data file that you create in step 5.

## Step 4: list the latest posts on the home page

Replace `_content/index.md`, so that the home page shows the two latest posts instead of a list written by hand:

```md title="_content/index.md"
---
title: Home
---
# Welcome to {{ site.title }}

{{ site.description }}

## Latest posts

{% assign posts = collections.post | reverse %}
{% for post in posts limit: 2 %}
- [{{ post.data.title }}]({{ post.data.url | replace: "index.html", "" }}): {{ post.data.description }}
{%- endfor %}

[All posts](/blog/)
```

## Step 5: generate a page per topic

The posts have topic tags besides `post`: `douze` and `liquid`. A page per topic lists its posts. Rather than writing
one page per topic, write one page that Douze **paginates** over a list of topics.

Create the list, `_data/topics.yml`:

```yaml title="_data/topics.yml"
- name: douze
  description: Posts about Douze itself.
- name: liquid
  description: Posts about the Liquid template language.
```

Then create `_content/tags.md`:

```md title="_content/tags.md"
---
title: Topic
pagination:
  data: topics
  size: 1
  alias: topic
permalink: /tags/{{ topic.name }}/
---
# Posts about {{ topic.name }}

{{ topic.description }}

<ul class="post-list">
{%- for post in collections[topic.name] %}
  <li><a href="{{ post.data.url | replace: "index.html", "" }}">{{ post.data.title }}</a></li>
{%- endfor %}
</ul>

[All posts](/blog/)
```

{% raw %}
`pagination` splits the `topics` list into pages of `size` items: one topic per page here. `alias: topic` names the
item of each page, and the permalink uses it to write each page to its own path, `/tags/douze/` and `/tags/liquid/`.
`collections[topic.name]` is the collection of that tag.
{% endraw %}

Open `http://localhost:8080/tags/douze/`: it lists the first two posts.

:::tip[Pages of several items]
With `size: 10` and a collection as `data`, such as `data: collections.post`, the same front matter paginates a long
list of posts into pages of ten: `pagination.items` holds the posts of each page, and `pagination.page.next` and
`pagination.page.previous` link the pages. See [Pagination](../guide/pagination.md).
:::

Link the topics from the posts. Replace `_includes/post.html`:

```html title="_includes/post.html" ins={7-12}
---
layout: base.html
---
<article class="post">
  <h1>{{ title }}</h1>
  <p class="meta">Published on <time datetime="{{ date | date: "%Y-%m-%d" }}">{{ date | date: "%B %-d, %Y" }}</time> by {{ author }}</p>
  <p class="tags">
    {%- for tag in tags %}
    {%- unless tag == "post" %}
    <a href="/tags/{{ tag }}/">#{{ tag }}</a>
    {%- endunless %}
    {%- endfor %}
  </p>
  {{ content }}
</article>
```

:::caution[Tags are not merged]
A page's `tags` replace those of its folder data: they are not added together. That is why each post lists `post`
itself, rather than `_content/posts/posts.yml` setting it for every post.
:::

## Step 6: generate an RSS feed

A feed is a template too. A file named `feed.xml.liquid` is rendered with Liquid, like a page, and written as
`feed.xml`. Create `_content/feed.xml.liquid`:

```liquid title="_content/feed.xml.liquid"
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0">
  <channel>
    <title>{{ site.title }}</title>
    <link>{{ "/" | absolute_url }}</link>
    <description>{{ site.description }}</description>
    {%- assign posts = collections.post | reverse %}
    {%- for post in posts %}
    <item>
      <title>{{ post.data.title }}</title>
      <link>{{ post.data.url | replace: "index.html", "" | absolute_url }}</link>
      <guid>{{ post.data.url | replace: "index.html", "" | absolute_url }}</guid>
      <pubDate>{{ post.data.date | date_to_rfc822 }}</pubDate>
      <description>{{ post.data.description }}</description>
    </item>
    {%- endfor %}
  </channel>
</rss>
```

{% raw %}
`absolute_url` writes `site.url` before a path, and `date_to_rfc822` writes a date as RSS wants it. Values are escaped
as in HTML pages, which is valid XML too. Layouts apply to HTML pages only, so the feed has none.
{% endraw %}

Open `http://localhost:8080/feed.xml`: the feed lists the three posts with their absolute links, such as
`https://example.com/posts/third-post/`.

## Step 7: generate a sitemap

A sitemap lists every page of the site for search engines. `collections.all` has every page. Create
`_content/sitemap.xml.liquid`:

```liquid title="_content/sitemap.xml.liquid"
<?xml version="1.0" encoding="utf-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
{%- for p in collections.all %}
{%- unless p.data.url == "/404.html" or p.data.pagination %}
  <url><loc>{{ p.data.url | replace: "index.html", "" | absolute_url }}</loc><lastmod>{{ p.data.page.date | date_to_xmlschema }}</lastmod></url>
{%- endunless %}
{%- endfor %}
</urlset>
```

Open `http://localhost:8080/sitemap.xml`. Two pages are left out: the 404 page, and `tags.md`, the template of the topic
pages, which `collections.all` holds once with the path of the template rather than one entry per topic. The feed and
the sitemap are not in the collections either, because templates such as `feed.xml.liquid` are not pages.

See [Feeds, sitemaps and other outputs](../guide/outputs.md), and the [RSS feed](../recipes/rss.md) and
[sitemap](../recipes/sitemap.md) recipes, for more.

:::tip[Check your understanding]

1. How does a template read the `title` of `_data/site.yml`?
2. In what order is `collections.post`, and how does the blog page show the newest post first?
3. How many pages does `_content/tags.md` produce, and where are they written?

:::

<details>
<summary>Answers</summary>

1. `site.title`: the file `site.yml` is the variable `site`.
2. By date, oldest first. The `reverse` filter reverses it.
3. One per topic of `_data/topics.yml`, so two, written to the path of the permalink: `/tags/douze/` and
   `/tags/liquid/`.

</details>

## Next

The blog is complete. In [unit 6](6-workflows.md), you will take control of how Douze builds it: add heading anchors, a
table of contents, highlighted code and search, then deploy it to GitHub Pages.
