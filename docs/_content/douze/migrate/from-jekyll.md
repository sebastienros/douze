---
title: Migrate from Jekyll
description: Map Jekyll's configuration, variables, posts, filters and plugins to Douze, and port a blog step by step.
liquid: false
---
Jekyll and Douze share Liquid, YAML front matter, `_includes`, `_data`, layouts that wrap `content`, and the
`relative_url`, `absolute_url`, `where_exp`, `jsonify` and `markdownify` filters. The main changes are the variable
names (Douze has no `page.` or `site.data.` prefix for front matter and data), the folders (layouts move into
`_includes`, posts lose the date in their file name), and the Ruby plugins, which Douze replaces with steps and
templates.

## Concepts

| Jekyll | Douze |
|---|---|
| `_config.yml` | `douze.yml` for the build ([Configuration](../guide/configuration.md)); site values in `_data/site.yml` |
| `url` and `baseurl` | `site.url` with the base path: `https://example.com/blog` |
| `site.title` and other `_config.yml` keys | `site.title` from `_data/site.yml` (or `site.json`) |
| `_site`, `.jekyll-cache` | `_site` (`-o`), `.douze` ([Cache](../guide/cache.md)) |
| `jekyll build`, `jekyll serve` (port 4000) | `douze`, `douze serve` (port 8080, `--port`) |
| `_layouts/default.html` | `_includes/default.html`; `layout: default` finds `default`, `default.liquid` or `default.html` |
| `layout.x` (a layout's front matter) | `x`: the front matter of layouts is a fallback for names the page does not define |
| `_includes/note.html` | the same |
| `{% include note.html kind="tip" %}`, `include.kind` | `{% include 'note.html', kind: "tip" %}`, `kind` |
| `{% include_relative x.md %}` | `{% include_markdown "./x.md" %}` for Markdown; nothing for other files |
| `_data/nav.yml`, `site.data.nav` | `_data/nav.yml`, `nav` |
| `page.title`, `page.x` | `title`, `x` |
| `page.url` | `url`, which ends with `index.html` |
| `page.date` | `date` (front matter); `page.date` is the file's modification time |
| `_posts/2024-03-05-hello.md` | any folder, with `date` in the front matter |
| `_drafts/`, `published: false` | `permalink: false` (in front matter, or in the folder's data file) |
| `site.posts` (newest first) | `collections.posts` (oldest first) with `tags: [posts]`; loop with `reversed` |
| `site.pages` | `collections.all` (HTML and Markdown pages) |
| `site.tags.x`, `page.tags` | `collections.x`, `tags` |
| `site.categories`, `categories` | none: use `tags` |
| Custom collections (`collections:` in `_config.yml`) | a folder whose data file sets `tags` |
| Front matter defaults (`defaults:`) | a data file in the folder: `blog/blog.yml` applies to every page under `blog/` |
| `permalink: pretty`, `/:year/:month/:title/` | a `permalink` template ([Routing](../guide/routing.md)) |
| `jekyll-paginate`, `paginator.posts` | `pagination` front matter, `pagination.items` ([Pagination](../guide/pagination.md)) |
| `post.excerpt`, `excerpt_separator` | the `excerpt` filter on HTML (`<!-- more -->`), or a `description` in front matter |
| `{% highlight ruby %}` | a fence and the [`highlight` step](../guide/syntax-highlighting.md) |
| `{% link %}`, `{% post_url %}` | a relative link to the `.md` file, rewritten to the page's url ([Markdown](../guide/markdown.md)) |
| kramdown | Markdig: enable `pipetables`, `footnotes`, `smartypants` and others in `markdown` |
| kramdown IAL `{: .note}`, auto ids | Markdig `attributes` (`{.note}`), the `headings` step |
| Sass (`_sass`, `assets/main.scss`) | not built in: a [command step](../guide/command-steps.md) that runs `sass` |
| `jekyll-feed`, `jekyll-sitemap` | a template ([RSS](../recipes/rss.md), [sitemap](../recipes/sitemap.md)) |
| `jekyll-seo-tag` | meta tags in the layout; images from the [`social-image` step](../guide/social-images.md) |
| Liquid only in files with front matter | every `.md`, `.html`, `.json`, `.xml`, `.txt` and `.liquid` page is Liquid; `liquid: false` turns it off |
| Underscore and dot files are skipped | every file of the content folder is published: use a `_content` folder |

## Filters

Douze has Fluid's standard Liquid filters (`date`, `where`, `sort`, `map`, `uniq`, `concat`, `slice`, `find`,
`url_encode`, `truncatewords`, `to_integer`...) and its own (`where_exp`, `group_by_exp`, `sort_by`, `index_by`,
`jsonify`, `xml_escape`, `markdownify`, `strip_html`, `absolute_url`, `relative_url`, `date_to_xmlschema`,
`date_to_rfc822`, `slug`). Jekyll filters that Douze does not have return their input unchanged, without an error:

| Jekyll | Douze |
|---|---|
| `slugify` | `slug` |
| `date_to_string` | `date: "%d %b %Y"` |
| `date_to_long_string` | `date: "%d %B %Y"` |
| `group_by: "year"` | `group_by_exp: "item", "item.year"` |
| `find_exp: "item", "..."` | `where_exp: "item", "..." \| first` |
| `array_to_sentence_string` | `join: ", "` |
| `cgi_escape`, `uri_escape` | `url_encode` |
| `number_of_words` | `strip_html \| split: " " \| size`, an approximation ([reading time](../recipes/reading-time.md)) |
| `normalize_whitespace`, `smartify`, `sample`, `inspect`, `push`, `pop` | none (`smartypants` for typographic quotes in Markdown) |
| `sassify`, `scssify` | none |

## Differences to watch for

- **HTML encoding.** Jekyll writes Liquid values as they are. Douze encodes them as HTML in pages: a value that
  holds HTML needs the `raw` filter. See [Liquid output is encoded](index.md#liquid-output-is-encoded).
- **Order.** `site.posts` is newest first; `collections.posts` is oldest first.
- **Links rewritten from `.md` files** are root-relative and do not include the path of `site.url`: a site served
  under `/blog/` writes its internal links with `relative_url` in templates.
- **Dates.** A front matter `date` without a time is midnight in the build machine's time zone, and Douze has no
  `timezone` setting: set `TZ` for the build, or write the time and offset (`2024-03-05T10:00:00+01:00`).
- **Includes.** The Jekyll form `{% include note.html kind="tip" %}` is a syntax error (`Invalid 'include' tag`).

## Port a site, step by step

The example is a minimal Jekyll blog: `_config.yml`, two layouts, a navigation data file, posts in `_posts`, an about
page and a stylesheet.

### 1. Move the folders

```bash
mkdir -p _content
git mv _layouts/* _includes/
git mv index.html about.md assets _content/
```

Keep `_data` and `_includes` at the root. Leave `Gemfile`, `README.md` and the other project files out of `_content`.

### 2. Replace `_config.yml`

The build settings go in `douze.yml`, the values templates read in `_data/site.yml`:

```yaml title="douze.yml"
site:
  url: https://example.com/blog-site

markdown:
  extensions: [pipetables, footnotes, attributes, autolinks]
```

```yaml title="_data/site.yml"
title: My blog
author: Jane
```

### 3. Move the posts

Jekyll takes a post's date and slug from its file name. Douze reads them from the front matter. This bash loop moves
every post into `_content/blog/` and writes its `date` and `slug`:

```bash
mkdir -p _content/blog
for f in _posts/*.md; do
  name=$(basename "$f" .md)
  sed "1a\\
date: ${name:0:10}\\
slug: ${name:11}
" "$f" > "_content/blog/${name:11}.md"
done
```

Posts whose front matter already has a `date` keep the first one; remove the duplicate line. A data file in the folder
gives every post its layout, its tag and Jekyll's URL:

```yaml title="_content/blog/blog.yml"
layout: post
tags: [posts]
permalink: "/{{ date | date: '%Y/%m/%d' }}/{{ slug }}/"
```

Without `permalink: pretty`, Jekyll's URLs end with `.html`: write `"/{{ date | date: '%Y/%m/%d' }}/{{ slug }}.html"`.

### 4. Rename variables in templates

Find the names to change:

```bash
grep -rnE 'page\.|site\.data\.|site\.posts|include\.|layout\.' _includes _content
```

The layouts after the port:

```liquid title="_includes/default.html"
<!doctype html>
<html>
<head><title>{{ title }} | {{ site.title }}</title><link rel="stylesheet" href="{{ '/assets/site.css' | relative_url }}"></head>
<body>
<nav>{% for item in navigation %}<a href="{{ item.url | relative_url }}">{{ item.title }}</a> {% endfor %}</nav>
{{ content }}
</body>
</html>
```

```liquid title="_includes/post.html"
---
layout: default
---
<article>
  <h1>{{ title }}</h1>
  <time datetime="{{ date | date_to_xmlschema }}">{{ date | date: "%d %B %Y" }}</time>
  {{ content }}
</article>
```

```liquid title="_content/index.html"
---
title: Home
layout: default
---
<ul>
{%- for post in collections.posts reversed %}
  <li><a href="{{ post.data.url | replace: 'index.html', '' | relative_url }}">{{ post.data.title }}</a></li>
{%- endfor %}
</ul>
```

```yaml title="_data/navigation.yml"
- title: Home
  url: /
- title: About
  url: /about/
```

`site.data.navigation` is `navigation`, `site.posts` is `collections.posts reversed`, and each post's values are under
`post.data`.

### 5. Replace tags and plugins

- Rewrite includes with parameters: `{% include note.html kind="tip" %}` is
  `{% include 'note.html', kind: "tip" %}`, and `include.kind` is `kind` in `note.html`.
- Replace `{% highlight %}` blocks with fences and add the `highlight` step, and `{% post_url %}` with links to the
  `.md` files.
- Write the feed and the sitemap as templates, and compile Sass with a command step.

### 6. Build and compare

```bash
bundle exec jekyll build -d old
douze --plain
diff -r old _site
```

## See also

- [Migration overview](index.md)
- [Data files](../guide/data.md)
- [Collections](../guide/collections.md)
- [Routing and permalinks](../guide/routing.md)
- [Layouts](../guide/layouts.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Template data](../reference/template-data.md)
