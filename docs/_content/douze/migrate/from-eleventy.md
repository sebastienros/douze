---
title: Migrate from Eleventy
description: Map Eleventy's folders, data, collections, pagination and plugins to Douze, and port a Liquid site step by step.
liquid: false
---
Douze follows Eleventy's model closely: `_includes` and `_data` folders, layouts that wrap `content`, directory data
files, `tags` that build `collections`, the same `pagination` front matter and the same collection order (date, then
source path). A site written in Liquid ports with few changes. Nunjucks, WebC and JavaScript templates, JavaScript data
files and everything registered in `eleventy.config.js` (filters, shortcodes, collections, plugins) have to be
rewritten.

## Concepts

| Eleventy | Douze |
|---|---|
| `eleventy.config.js` / `.eleventy.js` | `douze.yml`, optional ([Configuration](../guide/configuration.md)) |
| `dir.input` | `-s <dir>`, or `site.content` in `douze.yml` |
| `dir.output` (`_site`) | `-o <dir>` or `site.output` (default `_site`) |
| `dir.includes` (`_includes`), `dir.layouts` | `site.includes` (default `_includes`); layouts live there too |
| `dir.data` (`_data`) | `site.data` (default `_data`) |
| `pathPrefix` and the `url` filter | the path of `site.url` and the `relative_url` filter |
| Liquid templates (liquidjs) | Liquid templates (Fluid) |
| Nunjucks, WebC, 11ty.js, Handlebars, Pug templates | not supported: rewrite in Liquid |
| `markdownTemplateEngine: liquid` (the default) | Markdown is always rendered as Liquid first, fences excepted |
| `addPassthroughCopy` | not needed: files that no step claims are copied as they are |
| `.eleventyignore`, `.gitignore` | not read: keep the site in a content folder, or narrow the `inputs` of the workflow |
| Front matter (YAML, also JSON or JS) | YAML front matter only |
| `layout: layouts/post.njk` | `layout: layouts/post.liquid`, or `layouts/post` (tries `.liquid`, then `.html`) |
| `addLayoutAlias` | none: name the file |
| `{{ content \| safe }}` (Nunjucks) | `{{ content }}` |
| `{% include "x.liquid" %}`, `{% render "x.liquid", a: 1 %}` | the same, from the includes folder |
| `addShortcode`, `addPairedShortcode` | a Liquid file in `_includes/shortcodes/` ([Includes and shortcodes](../guide/shortcodes.md)) |
| `addFilter` | none: a shortcode, a computation in Liquid, or a [command step](../guide/command-steps.md) |
| Global data `_data/site.json` | the same, `site.title` |
| Global data `_data/*.js` | not supported: JSON, YAML or XML, or a file written by a command or script before the build |
| Directory data `posts/posts.json` | the same, and the file can have any name (`posts/defaults.yml`) |
| Template data `post.11tydata.json` | none: front matter, or a folder of its own |
| `eleventyComputed` | none: compute in the template |
| `tags`, `collections.all`, `collections.<tag>` | the same ([Collections](../guide/collections.md)) |
| `addCollection` | `where_exp`, `sort_by`, `group_by_exp` on `collections.all` |
| `eleventyExcludeFromCollections` | none; `permalink: false` removes a page from the build and the collections |
| Collection item `post.url`, `post.data.title` | `post.data.url` (with `index.html`), `post.data.title` |
| `post.date` | `post.data.date` (front matter), `post.data.page.date` (file time) |
| `post.content`, `post.templateContent` | `post.content` with a `collect` step that sets `content: true` |
| `page.url` | `url`, which ends with `index.html` |
| `page.date` (front matter `date`, else the file's creation time) | `date` is the front matter value; `page.date` is always the file's modification time |
| `page.fileSlug`, `page.inputPath`, `page.outputPath` | none |
| `permalink: /about/`, `permalink: false` | the same |
| `pagination` (`data`, `size`, `alias`, `resolve: values`) | the same ([Pagination](../guide/pagination.md)) |
| `pagination.href.next`, `pagination.href.previous` | `pagination.page.next.href`, `pagination.page.previous.href` |
| `pagination.filter`, `reverse`, `before`, `addAllPagesToCollections` | none |
| `getNextCollectionItem`, `getPreviousCollectionItem` | a loop that compares `p.data.url` with `url` (below) |
| Syntax highlighting plugin | the [`highlight` step](../guide/syntax-highlighting.md) |
| Image plugin | the `image` tag and the image steps ([Images](../guide/images.md)) |
| RSS plugin | a `feed.xml.liquid` template ([Add an RSS feed](../recipes/rss.md)) |
| Navigation plugin (`eleventyNavigation`) | a sidebar data file and the `nav_*` filters ([Navigation](../guide/navigation.md)) |
| i18n plugin | `site.locales` and the `localize` step ([Internationalization](../guide/internationalization.md)) |
| `--serve`, `--watch`, `--incremental` | `douze serve`, `douze watch`; every build is incremental ([Cache](../guide/cache.md)) |

## Differences to watch for

- **HTML encoding.** Eleventy's Liquid writes values as they are. Douze encodes what `{{ }}` writes as HTML: a value
  that holds HTML needs the `raw` filter. See [Liquid output is HTML-encoded](index.md#liquid-output-is-html-encoded).
- **`.liquid` pages.** A page named `blog.liquid` is written by Eleventy to `/blog/`. Douze writes a `.liquid` file
  without a second extension under its own name: rename it `blog.html` (HTML pages are rendered as Liquid too), or
  give it a `permalink`. `feed.xml.liquid` is written as `feed.xml`, as expected.
- **Filters.** Recent liquidjs versions have Jekyll's filters; Douze has only some of them: `slugify` is `slug`, and `group_by`,
  `push`, `array_to_sentence_string`, `number_of_words`, `date_to_string` do not exist and return their input
  unchanged. See [From Jekyll](from-jekyll.md#filters) for the full list.
- **Markdown.** Eleventy's markdown-it renders tables and strikethrough by default; Douze renders CommonMark until a
  `markdown` section enables extensions.
- **What is published.** Eleventy processes its template formats and copies only what `addPassthroughCopy` names.
  Douze copies every file of the content folder that no step claims, and treats `.json`, `.yml` and `.xml` files there
  as folder data.
- **Dates.** A front matter `date` without a time is midnight in the build machine's time zone.

## Port a site, step by step

The example is a small Eleventy blog with its sources in `src/`: two layouts, a directory data file for the posts, and
a paginated blog index.

### 1. Build with the source folder

Eleventy's `src/_includes` and `src/_data` are Douze's default folders when `src` is the source folder, so the site
builds without a `douze.yml`:

```bash
douze -s src -o _site
```

When the input folder is the project root, add a content folder rather than publishing the whole project: move the
pages into `_content/` (keeping `_includes` and `_data` at the root), or set `site.content`, `site.includes` and
`site.data` in `douze.yml`.

### 2. Convert the templates to Liquid

Rewrite Nunjucks templates in Liquid: `{% extends %}` and `{% block %}` become a layout chain (a layout's own front
matter names its layout), `{{ x | safe }}` becomes `{{ x | raw }}` where `x` holds HTML, and macros become includes or
shortcodes. Rename `blog.liquid` to `blog.html`.

```liquid title="src/_includes/layouts/base.liquid"
<!doctype html>
<html lang="en">
<head><title>{{ title }} | {{ metadata.title }}</title></head>
<body><main>{{ content }}</main></body>
</html>
```

```liquid title="src/_includes/layouts/post.liquid"
---
layout: layouts/base.liquid
---
<article>
  <h1>{{ title }}</h1>
  <time datetime="{{ date | date_to_xmlschema }}">{{ date | date: "%B %-d, %Y" }}</time>
  {{ content }}
</article>
```

The next post in a layout, as `getNextCollectionItem` gives it (`forloop.index` is the index of the next item):

```liquid
{%- for p in collections.posts %}
  {%- if p.data.url == url %}{% assign next = collections.posts[forloop.index] %}{% endif %}
{%- endfor %}
{%- if next %}<a href="{{ next.data.url | replace: 'index.html', '' }}">{{ next.data.title }}</a>{% endif %}
```

### 3. Keep the data files

`src/_data/metadata.json` is `metadata`, as in Eleventy. The directory data file keeps working:

```json title="src/posts/posts.json"
{ "tags": "posts", "layout": "layouts/post.liquid" }
```

Convert `.11tydata.js` and `_data/*.js` files to JSON or YAML, or have a script write them before the build
([Data files](../guide/data.md)).

### 4. Update collection loops and pagination

Items of `collections` have their front matter and `url` under `data`, and pagination links are under
`pagination.page`:

```liquid title="src/blog.html"
---
title: Blog
layout: layouts/base.liquid
pagination:
  data: collections.posts
  size: 10
  alias: posts
permalink: "/blog/{% if pagination.pageNumber > 0 %}{{ pagination.pageNumber | plus: 1 }}/{% endif %}"
---
<ul>
{%- for post in posts %}
  <li><a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a></li>
{%- endfor %}
</ul>
{%- if pagination.page.previous %}
<a href="{{ pagination.page.previous.href | replace: 'index.html', '' }}">Newer</a>
{%- endif %}
{%- if pagination.page.next %}
<a href="{{ pagination.page.next.href | replace: 'index.html', '' }}">Older</a>
{%- endif %}
```

Collections are sorted oldest first and pagination has no `reverse`: this index lists the oldest posts first. To list
the newest first without pagination, loop with `reversed`.

### 5. Replace filters, shortcodes and plugins

A shortcode registered in JavaScript becomes a Liquid file. `addShortcode("youtube", id => ...)` is:

```liquid title="src/_includes/shortcodes/youtube.liquid"
---
args: [id]
---
<iframe src="https://www.youtube-nocookie.com/embed/{{ id }}" title="YouTube video" allowfullscreen></iframe>
```

Pages call it the same way, `{% youtube "dQw4w9WgXcQ" %}`. Replace the syntax highlighting plugin with the `highlight`
step, the image plugin with the `image` tag, and the RSS plugin with a feed template.

### 6. Compare the output

Build both and compare:

```bash
npx @11ty/eleventy --input=src --output=old
douze -s src -o _site --plain
diff -r old _site
```

### Tag pages

Eleventy generates one page per tag with `pagination: { data: collections, size: 1, alias: tag, filter: [all] }`.
Douze's pagination has no `filter`, and `collections` also has `all` and `urls`, so paginating it writes
`/tags/all/` and `/tags/urls/` too. List the tags in a data file and paginate that instead:

```yaml title="src/_data/tagList.yml"
- posts
- notes
```

```liquid title="src/tags.html"
---
pagination:
  data: tagList
  size: 1
  alias: tag
permalink: "/tags/{{ tag | slug }}/"
---
<h1>{{ tag }}</h1>
{%- for post in collections[tag] %}
<a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a>
{%- endfor %}
```

## See also

- [Migration overview](index.md)
- [Collections](../guide/collections.md)
- [Pagination](../guide/pagination.md)
- [Includes and shortcodes](../guide/shortcodes.md)
- [Template data](../reference/template-data.md)
- [Liquid filters and tags](../reference/liquid.md)
- [`collect` step](../reference/steps/collect.md)
